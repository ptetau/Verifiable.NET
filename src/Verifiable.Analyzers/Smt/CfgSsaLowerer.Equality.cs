using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Verifiable.Analyzers.Smt;

/// <summary>
/// Equality and object construction: where C# runs user code (a declared <c>Equals</c>, an <c>operator ==</c>, a
/// constructor body, a property initializer) the SMT model must either run that code too or refuse the program.
/// </summary>
/// <remarks>
/// Structural SMT equality is only right for types whose C# equality is also structural: positional records,
/// tuples, primitives. A user-declared <c>Equals</c>/<c>operator ==</c> on a [Verifiable] type is modeled as a call
/// to that method (its contract, or its body when it is a single expression). Anything else that C# compares
/// differently from the SMT datatype (<c>ImmutableArray</c> compares array references, a class compares references,
/// a type with an unverified <c>Equals</c>) is rejected with VER101. <c>DateTimeOffset</c> compares its UTC instant
/// only, also inside a record or a tuple.
/// </remarks>
internal sealed partial class CfgSsaLowerer
{
    /// <summary>
    /// Handles <c>==</c> and <c>!=</c> that run user code: a user-declared operator, or a record struct whose
    /// <c>Equals(T)</c> is user-declared. Returns false when the ordinary lowering applies.
    /// </summary>
    private bool TryLowerUserEquality(
        IBinaryOperation bin,
        SmtTypedTerm lhs,
        SmtTypedTerm rhs,
        string gamma,
        bool emitObligations,
        int inlineDepth,
        out SmtTypedTerm result)
    {
        result = default!;
        var negate = bin.OperatorKind == BinaryOperatorKind.NotEquals;
        var op = bin.OperatorMethod;
        var boolType = _compilation.GetSpecialType(SpecialType.System_Boolean);

        if (op is not null && IsUserDeclaredOperator(op))
        {
            if (VerifiableSymbolHelpers.IsConstrainedStruct(op.ContainingType))
            {
                return false;
            }
            if (!VerifiableSymbolHelpers.IsTrustedPureMethod(op))
            {
                throw new ContractLoweringException(
                    $"The user-defined '{op.ContainingType.Name}' operator '{(negate ? "!=" : "==")}' is not [Verifiable], so its body is not proved and cannot stand in for the comparison; compare the fields instead, or mark the type [Verifiable].");
            }
            result = CallEqualityMethod(op, null, lhs, rhs, gamma, bin.Syntax.GetLocation(), emitObligations, inlineDepth);
            return true;
        }

        var type = UnwrapNullable(lhs.Type);
        if (type is INamedTypeSymbol { IsRecord: true } record && !VerifiableSymbolHelpers.IsConstrainedStruct(record) &&
            FindUserEquals(record) is { } userEquals)
        {
            if (!record.IsValueType || bin.IsLifted || !SymbolEqualityComparer.Default.Equals(UnwrapNullable(rhs.Type), type))
            {
                throw new ContractLoweringException(
                    $"Record '{record.Name}' declares its own Equals, so '{(negate ? "!=" : "==")}' runs user code that is not modeled; call Equals explicitly, or compare the fields.");
            }
            if (!VerifiableSymbolHelpers.IsTrustedPureMethod(userEquals))
            {
                throw new ContractLoweringException(
                    $"Record '{record.Name}' declares its own Equals, which is not [Verifiable], so its body is not proved; mark the record [Verifiable] or compare the fields.");
            }
            result = CallEqualityMethod(userEquals, lhs, lhs, rhs, gamma, bin.Syntax.GetLocation(), emitObligations, inlineDepth);
            if (negate)
            {
                result = new SmtTypedTerm($"(not {result.Term})", boolType);
            }
            return true;
        }

        if (IsDefaultOperand(bin.LeftOperand) != IsDefaultOperand(bin.RightOperand) &&
            UnwrapNullable(lhs.Type) is INamedTypeSymbol { OriginalDefinition: { } arrDef } arrType &&
            arrDef.ToDisplayString() == "System.Collections.Immutable.ImmutableArray<T>" &&
            _types.TryGetSequenceInfo(arrType, out _, out var seqId, out _, out _) &&
            !_types.TryGetNullableInfo(lhs.Type, out _, out _, out _) && !_types.TryGetNullableInfo(rhs.Type, out _, out _, out _))
        {
            // `array == default` asks whether the underlying array is null: exactly the modeled default flag.
            var other = IsDefaultOperand(bin.LeftOperand) ? rhs : lhs;
            var test = $"(seq_is_default_{seqId} {other.Term})";
            result = new SmtTypedTerm(negate ? $"(not {test})" : test, boolType);
            return true;
        }

        return false;
    }

    private static bool IsDefaultOperand(IOperation operand)
    {
        while (operand is IConversionOperation { IsImplicit: true } conv)
        {
            operand = conv.Operand;
        }
        return operand is IDefaultValueOperation;
    }

    private SmtTypedTerm CallEqualityMethod(
        IMethodSymbol method,
        SmtTypedTerm? receiver,
        SmtTypedTerm left,
        SmtTypedTerm right,
        string gamma,
        Location loc,
        bool emitObligations,
        int inlineDepth)
    {
        var args = receiver is null
            ? new[] { _translator.ConvertTo(left, method.Parameters[0].Type), _translator.ConvertTo(right, method.Parameters[1].Type) }
            : new[] { _translator.ConvertTo(right, method.Parameters[0].Type) };
        if (!_expandingCalls.Add(method.OriginalDefinition))
        {
            throw new ContractLoweringException($"Recursive specification expansion of '{method.Name}' is not supported.");
        }
        try
        {
            return LowerHelperCall(method, receiver, args, gamma, loc, emitObligations, inlineDepth);
        }
        finally
        {
            _expandingCalls.Remove(method.OriginalDefinition);
        }
    }

    /// <summary>An operator written by the user (not synthesized for a record, not part of the framework).</summary>
    private static bool IsUserDeclaredOperator(IMethodSymbol op)
    {
        if (op.MethodKind != MethodKind.UserDefinedOperator || op.Parameters.Length != 2)
        {
            return false;
        }
        return IsUserDeclared(op) && !IsFrameworkType(op.ContainingType);
    }

    /// <summary>True when a person wrote this member: it is not compiler-synthesized, in this compilation or in metadata.</summary>
    private static bool IsUserDeclared(IMethodSymbol method)
    {
        if (method.IsImplicitlyDeclared)
        {
            return false;
        }
        return method.DeclaringSyntaxReferences.Length > 0 ||
               !method.GetAttributes().Any(a => a.AttributeClass?.Name == "CompilerGeneratedAttribute");
    }

    private static bool IsFrameworkType(ITypeSymbol type)
    {
        var ns = type.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        return ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal) ||
               ns == "Microsoft" || ns.StartsWith("Microsoft.", StringComparison.Ordinal);
    }

    private static ITypeSymbol UnwrapNullable(ITypeSymbol type) =>
        type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } n ? n.TypeArguments[0] : type;

    /// <summary>The <c>Equals</c> a user wrote on <paramref name="type"/> (taking the type or <c>object</c>), if any.</summary>
    private static IMethodSymbol? FindUserEquals(INamedTypeSymbol type) =>
        type.GetMembers("Equals").OfType<IMethodSymbol>().FirstOrDefault(m =>
            !m.IsStatic && m.Parameters.Length == 1 && IsUserDeclared(m) &&
            (SymbolEqualityComparer.Default.Equals(UnwrapNullable(m.Parameters[0].Type), type) ||
             m.Parameters[0].Type.SpecialType == SpecialType.System_Object));

    /// <summary>
    /// Why C# equality on <paramref name="type"/> is not what structural SMT equality says, or null when it is.
    /// <paramref name="viaOperator"/> is true when the comparison is written with <c>==</c> (a tuple element), false
    /// when it goes through <c>Equals</c> (a field of a record).
    /// </summary>
    private string? UnmodeledEqualityReason(ITypeSymbol type, bool viaOperator, HashSet<ITypeSymbol>? seen = null)
    {
        type = UnwrapNullable(type);
        if (type.SpecialType != SpecialType.None || type.TypeKind is TypeKind.Enum or TypeKind.TypeParameter or TypeKind.Array or
            TypeKind.Dynamic or TypeKind.Pointer or TypeKind.FunctionPointer or TypeKind.Error ||
            CalendarArithmetic.Classify(type) != CalendarTypeKind.None ||
            type is not INamedTypeSymbol named)
        {
            return null;
        }

        var original = named.OriginalDefinition.ToDisplayString();
        if (original == "System.Collections.Immutable.ImmutableArray<T>")
        {
            return "ImmutableArray equality compares the underlying array references, not the elements; compare with SequenceEqual or lengths";
        }
        if (original.StartsWith("System.Collections.Immutable.", StringComparison.Ordinal))
        {
            // Other immutable collections compare by reference too, but the existing models and samples treat them as
            // values (a "board unchanged" postcondition); that is a known approximation, left as it was.
            return null;
        }

        seen ??= new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        if (!seen.Add(named))
        {
            return null;
        }

        if (named.IsTupleType)
        {
            foreach (var element in named.TupleElements)
            {
                if (UnmodeledEqualityReason(element.Type, viaOperator, seen) is { } elementReason)
                {
                    return elementReason;
                }
            }
            return null;
        }

        if (IsFrameworkType(named) || VerifiableSymbolHelpers.IsConstrainedStruct(named))
        {
            return null;
        }

        if (viaOperator && named.GetMembers().OfType<IMethodSymbol>().Any(IsUserDeclaredOperator))
        {
            return $"'{named.Name}' declares its own operator ==, which is not modeled here";
        }

        if (FindUserEquals(named) is not null)
        {
            return $"'{named.Name}' declares its own Equals, which is not modeled here; compare the fields instead";
        }

        if (!named.IsRecord && !named.IsValueType)
        {
            return $"'{named.Name}' is compared by reference; compare individual fields instead";
        }

        if (named.IsRecord && named.TypeKind == TypeKind.Class &&
            named.GetMembers("EqualityContract").OfType<IPropertySymbol>().Any(p => !p.IsImplicitlyDeclared && p.DeclaringSyntaxReferences.Length > 0))
        {
            return $"'{named.Name}' declares its own EqualityContract, which is not modeled here";
        }

        if (_types.TryGetComposite(named, out var comp) && comp is not null)
        {
            foreach (var field in comp.Fields)
            {
                if (UnmodeledEqualityReason(field.Type, viaOperator: false, seen) is { } fieldReason)
                {
                    return $"field '{field.Name}': {fieldReason}";
                }
            }
        }

        return null;
    }

    private void ThrowIfEqualityUnmodeled(ITypeSymbol type, bool viaOperator)
    {
        if (UnmodeledEqualityReason(type, viaOperator) is { } reason)
        {
            throw new ContractLoweringException($"Equality on '{type.Name}' cannot be modeled: {reason}.");
        }
    }

    /// <summary>Structural equality for two terms of <paramref name="type"/>, comparing a DateTimeOffset by instant only.</summary>
    private string EqualityTerm(string l, string r, ITypeSymbol type) =>
        EqualityTerm(l, r, type, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default));

    private string EqualityTerm(string l, string r, ITypeSymbol type, HashSet<ITypeSymbol> seen)
    {
        if (!ContainsDateTimeOffset(type, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default)))
        {
            return $"(= {l} {r})";
        }
        if (CalendarArithmetic.Classify(type) == CalendarTypeKind.DateTimeOffset)
        {
            return $"(= (dto_utc_ticks {l}) (dto_utc_ticks {r}))";
        }
        if (_types.TryGetNullableInfo(type, out _, out var optId, out var inner))
        {
            var inside = EqualityTerm($"(opt-val-{optId} {l})", $"(opt-val-{optId} {r})", inner!, seen);
            return $"(or (and (= {l} opt-none-{optId}) (= {r} opt-none-{optId})) " +
                   $"(and (not (= {l} opt-none-{optId})) (not (= {r} opt-none-{optId})) {inside}))";
        }
        if (seen.Add(type) && _types.TryGetComposite(type, out var comp) && comp is not null)
        {
            var parts = comp.Fields.Select(f => EqualityTerm($"({f.AccessorName} {l})", $"({f.AccessorName} {r})", f.Type, seen)).ToList();
            seen.Remove(type);
            return parts.Count switch
            {
                0 => "true",
                1 => parts[0],
                _ => $"(and {string.Join(" ", parts)})"
            };
        }
        return $"(= {l} {r})";
    }

    private bool ContainsDateTimeOffset(ITypeSymbol type, HashSet<ITypeSymbol> seen)
    {
        type = UnwrapNullable(type);
        if (CalendarArithmetic.Classify(type) == CalendarTypeKind.DateTimeOffset)
        {
            return true;
        }
        if (type.SpecialType != SpecialType.None || type.TypeKind is TypeKind.Enum or TypeKind.TypeParameter || !seen.Add(type))
        {
            return false;
        }
        return _types.TryGetComposite(type, out var comp) && comp is not null &&
               comp.Fields.Any(f => ContainsDateTimeOffset(f.Type, seen));
    }

    /// <summary>
    /// Lowers <c>(a, b) == (c, d)</c> as <c>a == c &amp;&amp; b == d</c> (and <c>!=</c> as the negation), as C# defines it.
    /// </summary>
    private SmtTypedTerm LowerTupleBinary(ITupleBinaryOperation tupleBin, string gamma, BlockEnv env, bool emitObligations, int inlineDepth)
    {
        var boolType = _compilation.GetSpecialType(SpecialType.System_Boolean);
        var loc = tupleBin.Syntax.GetLocation();
        if (tupleBin.OperatorKind is not (BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals))
        {
            throw new ContractLoweringException($"Unsupported tuple operator '{tupleBin.OperatorKind}' in '{tupleBin.Syntax}'.");
        }

        var left = TupleOperands(tupleBin.LeftOperand, gamma, env, emitObligations, inlineDepth);
        var right = TupleOperands(tupleBin.RightOperand, gamma, env, emitObligations, inlineDepth);
        if (left.Count != right.Count || left.Count == 0)
        {
            throw new ContractLoweringException($"Unsupported tuple comparison '{tupleBin.Syntax}'.");
        }

        var parts = new List<string>(left.Count);
        for (var i = 0; i < left.Count; i++)
        {
            var reason = UnmodeledEqualityReason(left[i].Type, viaOperator: true) ??
                         UnmodeledEqualityReason(right[i].Type, viaOperator: true);
            if (reason is not null)
            {
                throw new ContractLoweringException($"Tuple element {i + 1} of '{tupleBin.Syntax}' cannot be compared: {reason}.");
            }
            parts.Add(LowerBinaryOperator(BinaryOperatorKind.Equals, left[i], right[i], boolType, gamma, loc, emitObligations).Term);
        }

        var all = parts.Count == 1 ? parts[0] : $"(and {string.Join(" ", parts)})";
        return new SmtTypedTerm(tupleBin.OperatorKind == BinaryOperatorKind.Equals ? all : $"(not {all})", boolType);
    }

    private List<SmtTypedTerm> TupleOperands(IOperation operand, string gamma, BlockEnv env, bool emitObligations, int inlineDepth)
    {
        while (operand is IConversionOperation { IsImplicit: true } conv && conv.Operand.Type is { IsTupleType: true })
        {
            operand = conv.Operand;
        }

        if (operand is ITupleOperation literal)
        {
            return literal.Elements.Select(e => LowerExpression(e, gamma, env, emitObligations, inlineDepth)).ToList();
        }

        var whole = LowerExpression(operand, gamma, env, emitObligations, inlineDepth);
        if (!_types.TryGetComposite(whole.Type, out var comp) || comp is null || comp.Fields.Count == 0)
        {
            throw new ContractLoweringException($"Unsupported tuple operand '{operand.Syntax}' in comparison.");
        }
        return comp.Fields.Select(f => new SmtTypedTerm($"({f.AccessorName} {whole.Term})", f.Type)).ToList();
    }

    /// <summary>
    /// Why an object creation cannot be modeled as a field projection, or null when it can: a constructor body or a
    /// property initializer is user code that the projection would silently skip.
    /// </summary>
    private string? UnmodeledConstructionReason(IObjectCreationOperation objCreate)
    {
        var targetType = objCreate.Type!;
        if (targetType.DeclaringSyntaxReferences.Length == 0 || VerifiableSymbolHelpers.IsConstrainedStruct(targetType))
        {
            return null;
        }
        return InitializerProjection.IsSupportedProjection(objCreate, _types, out var reason)
            ? null
            : reason;
    }

    /// <summary>
    /// Lowers a constructor written in this compilation when its body is straight-line field assignments from its
    /// parameters (after the leading <c>Spec</c> contracts): its <c>Spec.Requires</c> are checked at the call, and the
    /// fields take the assigned values. Anything else (a branch, a chained constructor, a property initializer that
    /// is not a plain copy of the parameter) is refused with <paramref name="why"/>.
    /// </summary>
    private Dictionary<string, SmtTypedTerm> LowerSourceConstructor(
        IObjectCreationOperation objCreate,
        SmtCompositeInfo comp,
        string why,
        string gamma,
        BlockEnv env,
        bool emitObligations,
        int inlineDepth)
    {
        var ctor = objCreate.Constructor;
        var refusal = $"{why} A constructor is followed only when its body is plain 'field = expression' assignments from its parameters; use a positional record, or compute the value outside the constructor.";
        if (ctor is null || objCreate.Initializer is not null || inlineDepth >= 8 ||
            ctor.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not Microsoft.CodeAnalysis.CSharp.Syntax.ConstructorDeclarationSyntax { Initializer: null } decl)
        {
            throw new ContractLoweringException(refusal);
        }

        var statements = decl.Body is not null
            ? decl.Body.Statements.OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ExpressionStatementSyntax>().Select(s => s.Expression).ToList()
            : new List<Microsoft.CodeAnalysis.CSharp.Syntax.ExpressionSyntax> { decl.ExpressionBody!.Expression };
        if (decl.Body is not null && statements.Count != decl.Body.Statements.Count)
        {
            throw new ContractLoweringException(refusal);
        }

        var calleeEnv = new BlockEnv();
        var args = new List<SmtTypedTerm>(objCreate.Arguments.Length);
        foreach (var arg in objCreate.Arguments.OrderBy(a => a.Parameter?.Ordinal ?? 0))
        {
            if (arg.Parameter is null)
            {
                throw new ContractLoweringException(refusal);
            }
            var value = _translator.ConvertTo(LowerExpression(arg.Value, gamma, env, emitObligations, inlineDepth), arg.Parameter.Type);
            calleeEnv.Vars[arg.Parameter] = value;
            args.Add(value);
        }

        var specs = ExtractMethodSpecs(_compilation, ctor, default);
        _errors.AddRange(specs.Errors);
        foreach (var req in specs.Requires)
        {
            if (req.StubMethod is not null)
            {
                CrossAssemblyContractCache.BindStubParameters(calleeEnv.Vars, ctor, req.StubMethod, args);
            }
        }
        var bodyGamma = CheckMethodRequires(ctor, specs, calleeEnv, gamma, objCreate.Syntax.GetLocation()).Aggregate(gamma, CombineAnd);

        var model = _compilation.GetSemanticModel(decl.SyntaxTree);
        var assigned = new Dictionary<string, SmtTypedTerm>(StringComparer.OrdinalIgnoreCase);
        foreach (var statement in statements)
        {
            var op = model.GetOperation(statement);
            if (op is IInvocationOperation inv && inv.TargetMethod.ContainingType?.ToDisplayString() == VerifiableSymbolHelpers.SpecTypeName)
            {
                continue;
            }
            if (op is not ISimpleAssignmentOperation { Target: IMemberReferenceOperation { Instance: IInstanceReferenceOperation } target } assign ||
                !comp.Fields.Any(f => f.Name == target.Member.Name) ||
                assign.Value.DescendantsAndSelf().Any(d => d is IInstanceReferenceOperation))
            {
                throw new ContractLoweringException(refusal);
            }
            var field = comp.Fields.First(f => f.Name == target.Member.Name);
            assigned[field.Name] = _translator.ConvertTo(LowerExpression(assign.Value, bodyGamma, calleeEnv, emitObligations, inlineDepth + 1), field.Type);
        }

        foreach (var field in comp.Fields)
        {
            if (!assigned.ContainsKey(field.Name) &&
                InitializerProjection.HasNonDefaultMemberInitializer((INamedTypeSymbol)objCreate.Type!, field.Name))
            {
                throw new ContractLoweringException(refusal);
            }
        }

        return assigned;
    }
}
