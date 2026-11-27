using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Verifiable.Analyzers.Smt;

internal sealed partial class CfgSsaLowerer
{
    private bool _queryCountDeclared;

    private static ContractLoweringException UnsupportedQuery(IOperation op) =>
        new($"Unsupported immutable LINQ query expression '{op.Syntax}'.");

    private static IOperation QueryArgument(IInvocationOperation call, int ordinal) =>
        call.Arguments.Single(a => a.Parameter?.Ordinal == ordinal).Value;

    private static IOperation QuerySource(IInvocationOperation call)
    {
        var source = QueryArgument(call, 0);
        // Enumerable inserts an implicit interface conversion (boxing ImmutableArray).
        // Don't erase explicit casts or user-defined conversions.
        if (source is IConversionOperation { IsImplicit: true, OperatorMethod: null } conv &&
            conv.Type?.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)
            source = conv.Operand;
        return source;
    }

    private (IParameterSymbol Param, IOperation Body) QueryLambda(IInvocationOperation call)
    {
        if (call.Arguments.Length != 2 ||
            QueryArgument(call, 1) is not IDelegateCreationOperation del)
            throw UnsupportedQuery(call);
        // CFG lambda bodies live in separate graphs. Recover just the original bound
        // lambda, not the enclosing query (whose captures reflect current SSA values).
        var target = del.Target is IFlowAnonymousFunctionOperation flow
            ? _compilation.GetSemanticModel(flow.Syntax.SyntaxTree).GetOperation(flow.Syntax)
            : del.Target;
        if (target is not IAnonymousFunctionOperation { Symbol.Parameters.Length: 1, Body.Operations.Length: 1 } lambda ||
            lambda.Body.Operations[0] is not IReturnOperation { ReturnedValue: { } value })
            throw UnsupportedQuery(call);
        return (lambda.Symbol.Parameters[0], value);
    }

    private SmtTypedTerm LowerImmutableQuery(IInvocationOperation terminal, string gamma, BlockEnv env,
        bool emitObligations, int inlineDepth)
    {
        var name = terminal.TargetMethod.Name;
        if (name is not ("Any" or "All" or "Count") || terminal.Arguments.Length is not (1 or 2))
            throw UnsupportedQuery(terminal);

        var source = QuerySource(terminal);
        IInvocationOperation? grouping = null;
        if (source is IInvocationOperation group && VerifiableSymbolHelpers.IsLinqProvider(group.TargetMethod) &&
            group.TargetMethod.Name == "GroupBy")
        {
            if (name != "Any" || !IsDuplicateGroupTest(QueryLambda(terminal)))
                throw UnsupportedQuery(terminal);
            grouping = group;
            source = QuerySource(group);
        }

        var stages = new Stack<IInvocationOperation>();
        while (source is IInvocationOperation stage && VerifiableSymbolHelpers.IsLinqProvider(stage.TargetMethod))
        {
            if (stages.Count >= 32)
                throw new ContractLoweringException("Immutable LINQ query exceeds the 32-stage pipeline limit.");
            if (stage.TargetMethod.Name is not ("Select" or "Where"))
                throw UnsupportedQuery(stage);
            stages.Push(stage);
            source = QuerySource(stage);
        }

        if (source.Type?.ContainingAssembly?.Identity is not { Name: "System.Collections.Immutable" } assembly ||
            !VerifiableSymbolHelpers.IsFrameworkLinqAssembly(assembly) ||
            source is IConversionOperation ||
            !_types.TryGetSequenceInfo(source.Type, out _, out var seqId, out var elemType))
            throw UnsupportedQuery(source);
        var sequence = LowerExpression(source, gamma, env, emitObligations, inlineDepth);
        if (source.Type.IsValueType)
            AddObligation("Precondition", "ImmutableArray must not be default before enumeration", source.Syntax.GetLocation(),
                gamma, $"(not (= {sequence.Term} default-{_types.GetSort(source.Type)}))");
        var count = $"(seq_count_{seqId} {sequence.Term})";
        var index = "linq_i";
        var domain = $"(and (<= 0 {index}) (< {index} {count}))";
        var element = new SmtTypedTerm($"(select (seq_elems_{seqId} {sequence.Term}) {index})", elemType!);
        var filter = "true";
        foreach (var stage in stages)
        {
            var term = Apply(QueryLambda(stage));
            if (stage.TargetMethod.Name == "Where")
                filter = CombineAnd(filter, term.Term);
            else
                element = term;
        }

        string result;
        if (grouping is not null)
        {
            var key = Apply(QueryLambda(grouping));
            if (!IsQueryScalar(key.Type)) throw UnsupportedQuery(grouping);
            // Capture the key and filter at i, then rebind the index for j rather
            // than performing textual substitution in SMT terms (including literals).
            var match = $"(let ((linq_key {key.Term})) (exists ((linq_j Int)) " +
                $"(and (< {index} linq_j) (< linq_j {count}) " +
                $"(let (({index} linq_j)) (and {filter} (= linq_key {key.Term}))))))";
            result = $"(exists (({index} Int)) (and {domain} {filter} {match}))";
        }
        else
        {
            var pred = terminal.Arguments.Length == 2 ? Apply(QueryLambda(terminal)).Term : "true";
            var matches = CombineAnd(filter, pred);
            result = name switch
            {
                "All" => $"(forall (({index} Int)) (=> (and {domain} {filter}) {pred}))",
                "Any" => $"(exists (({index} Int)) (and {domain} {matches}))",
                _ => CountMatches(matches)
            };
        }
        return new SmtTypedTerm(result, terminal.Type!);

        SmtTypedTerm Apply((IParameterSymbol Param, IOperation Body) lambda)
        {
            if (!SymbolEqualityComparer.Default.Equals(lambda.Param.Type, element.Type))
                throw UnsupportedQuery(lambda.Body);
            var scope = env.Clone();
            // Repeated references to the parameter must not duplicate the whole
            // preceding Select pipeline (e.g. a chain of Select(x => x == x)).
            scope.Vars[lambda.Param] = new SmtTypedTerm("linq_value", element.Type);
            var body = LowerQueryValue(lambda.Body, scope);
            return new SmtTypedTerm($"(let ((linq_value {element.Term})) {body.Term})", body.Type);
        }

        string CountMatches(string matches)
        {
            var term = count;
            if (matches != "true")
            {
                if (!_queryCountDeclared)
                {
                    _decls.Add("(define-fun-rec linq_count ((p (Array Int Bool)) (n Int)) Int " +
                        "(ite (<= n 0) 0 (+ (linq_count p (- n 1)) (ite (select p (- n 1)) 1 0))))");
                    _queryCountDeclared = true;
                }
                term = $"(linq_count (lambda (({index} Int)) {matches}) {count})";
                // Immediate consequences of the recurrence. Z3 needn't discover
                // induction on arbitrary lengths to prove common cardinality bounds.
                AddAssertion($"(=> (>= {count} 0) (and (<= 0 {term}) (<= {term} {count}) " +
                    $"(= (> {term} 0) (exists (({index} Int)) (and {domain} {matches})))))",
                    "CollectionAxiom", "Filtered sequence cardinality", terminal.Syntax.GetLocation());
            }
            return _types.UseBitVec ? $"((_ int2bv 32) {term})" : term;
        }
    }

    private static bool IsDuplicateGroupTest((IParameterSymbol Param, IOperation Body) lambda) =>
        lambda.Body is IBinaryOperation
        {
            OperatorKind: BinaryOperatorKind.GreaterThan, OperatorMethod: null,
            RightOperand.ConstantValue: { HasValue: true, Value: 1 },
            LeftOperand: IInvocationOperation { Arguments.Length: 1, TargetMethod.Name: "Count" } count
        } && VerifiableSymbolHelpers.IsLinqProvider(count.TargetMethod) &&
        QuerySource(count) is IParameterReferenceOperation p && SymbolEqualityComparer.Default.Equals(lambda.Param, p.Parameter);

    private static bool IsQueryScalar(ITypeSymbol? type) =>
        type?.TypeKind == TypeKind.Enum || type?.SpecialType is SpecialType.System_Boolean or SpecialType.System_String
            or SpecialType.System_Int32 or SpecialType.System_Int64 or SpecialType.System_Decimal;

    // Only build terms here: ordinary lowering can emit global SSA assertions and
    // bounds obligations, which cannot contain an index bound inside a quantifier.
    private SmtTypedTerm LowerQueryValue(IOperation op, BlockEnv env)
    {
        switch (op)
        {
            case ILocalReferenceOperation l when env.Vars.TryGetValue(l.Local, out var local):
                return local;
            case IParameterReferenceOperation p when env.Vars.TryGetValue(p.Parameter, out var arg):
                return arg;
            case ILiteralOperation lit when IsQueryScalar(lit.Type):
                return LowerLiteralOperation(lit);
            case IFieldReferenceOperation { Instance: null, Field: { HasConstantValue: true } constant }
                when IsQueryScalar(constant.Type):
                return LowerConstant(constant.Type, constant.ConstantValue);
            case IParenthesizedOperation paren:
                return LowerQueryValue(paren.Operand, env);
            case IConversionOperation { OperatorMethod: null, IsImplicit: true } conversion
                when conversion.Conversion.IsIdentity || conversion.Conversion.IsNumeric && IsQueryScalar(conversion.Operand.Type) &&
                    IsQueryScalar(conversion.Type):
                return _translator.ConvertTo(LowerQueryValue(conversion.Operand, env), conversion.Type!);
            case IPropertyReferenceOperation { Instance: { } obj, Property: { IsIndexer: false } prop }
                when obj.Type?.IsValueType == true && prop.DeclaringSyntaxReferences.Any(r =>
                    r.GetSyntax() is ParameterSyntax || r.GetSyntax() is PropertyDeclarationSyntax { AccessorList: { } accessors }
                        && accessors.Accessors.All(a => a.Body is null && a.ExpressionBody is null)):
                return _translator.TryTranslateMemberOnReceiver(LowerQueryValue(obj, env), prop.Name) ?? throw UnsupportedQuery(op);
            case IFieldReferenceOperation { Instance: { } structValue, Field: { IsStatic: false } field }
                when structValue.Type?.IsValueType == true:
                return _translator.TryTranslateMemberOnReceiver(LowerQueryValue(structValue, env), field.Name) ?? throw UnsupportedQuery(op);
            case IUnaryOperation { OperatorKind: UnaryOperatorKind.Not, OperatorMethod: null } un
                when un.Type?.SpecialType == SpecialType.System_Boolean:
                return new SmtTypedTerm($"(not {LowerQueryValue(un.Operand, env).Term})", un.Type);
            case IBinaryOperation b when !b.IsLifted && (b.OperatorMethod is null ||
                    b.OperatorMethod.ContainingType.SpecialType is SpecialType.System_String or SpecialType.System_Decimal) &&
                IsQueryScalar(b.LeftOperand.Type) && IsQueryScalar(b.RightOperand.Type):
                var left = LowerQueryValue(b.LeftOperand, env);
                var right = LowerQueryValue(b.RightOperand, env);
                if (b.OperatorKind is BinaryOperatorKind.ConditionalAnd or BinaryOperatorKind.ConditionalOr)
                    return new SmtTypedTerm($"({(b.OperatorKind == BinaryOperatorKind.ConditionalAnd ? "and" : "or")} {left.Term} {right.Term})", b.Type!);
                if (b.OperatorKind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals or BinaryOperatorKind.LessThan
                    or BinaryOperatorKind.GreaterThan or BinaryOperatorKind.LessThanOrEqual or BinaryOperatorKind.GreaterThanOrEqual)
                    return LowerBinaryOperator(b.OperatorKind, left, right, b.Type!, "true", op.Syntax.GetLocation(), false);
                break;
        }
        throw UnsupportedQuery(op);
    }
}
