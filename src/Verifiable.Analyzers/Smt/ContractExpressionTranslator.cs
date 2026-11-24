using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Verifiable.Analyzers.Smt;

internal sealed class ContractLoweringException(string message) : Exception(message);

internal sealed record SmtTypedTerm(string Term, ITypeSymbol Type);

internal sealed class ContractExpressionTranslator
{
    private readonly Compilation _compilation;
    private readonly SmtTypeSystem _types;
    private readonly INamedTypeSymbol? _enclosingType;
    private readonly IReadOnlyList<string> _usingNamespaces;

    public ContractExpressionTranslator(
        Compilation compilation,
        SmtTypeSystem types,
        INamedTypeSymbol? enclosingType,
        SyntaxTree? contextTree)
    {
        _compilation = compilation;
        _types = types;
        _enclosingType = enclosingType;
        _usingNamespaces = CollectUsings(contextTree);
    }

    public SmtTypedTerm TranslateContractString(
        string expressionText,
        IReadOnlyDictionary<string, SmtTypedTerm> env,
        ITypeSymbol? expectedType = null)
    {
        if (string.IsNullOrWhiteSpace(expressionText))
        {
            throw new ContractLoweringException("Contract expression cannot be empty.");
        }

        var expr = SyntaxFactory.ParseExpression(expressionText);
        var diag = expr.GetDiagnostics().FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error);
        if (diag is not null || expr.FullSpan.Length != expressionText.Length)
        {
            throw new ContractLoweringException(diag?.GetMessage() ?? "Syntax error in contract expression.");
        }

        return TranslateExpression(expr, env, expectedType);
    }

    public SmtTypedTerm TranslateExpression(
        ExpressionSyntax expr,
        IReadOnlyDictionary<string, SmtTypedTerm> env,
        ITypeSymbol? expectedType = null)
    {
        switch (expr)
        {
            case ParenthesizedExpressionSyntax paren:
                return TranslateExpression(paren.Expression, env, expectedType);

            case IdentifierNameSyntax id:
                return TranslateIdentifier(id.Identifier.ValueText, env);

            case ThisExpressionSyntax:
                if (env.TryGetValue("this", out var thisTerm))
                    return thisTerm;
                throw new ContractLoweringException("'this' is not available in this static context.");

            case LiteralExpressionSyntax lit:
                return TranslateLiteral(lit, expectedType);

            case PrefixUnaryExpressionSyntax pre:
                return TranslatePrefixUnary(pre, env, expectedType);

            case BinaryExpressionSyntax bin:
                return TranslateBinary(bin, env);

            case ConditionalExpressionSyntax cond:
                var cTerm = TranslateExpression(cond.Condition, env, _compilation.GetSpecialType(SpecialType.System_Boolean));
                var tTerm = TranslateExpression(cond.WhenTrue, env, expectedType);
                var fTerm = TranslateExpression(cond.WhenFalse, env, tTerm.Type);
                var (promotedT, promotedF, resType) = UnifyNumericPair(tTerm, fTerm);
                return new SmtTypedTerm($"(ite {cTerm.Term} {promotedT} {promotedF})", resType);

            case MemberAccessExpressionSyntax ma:
                return TranslateMemberAccess(ma, env);

            case ElementAccessExpressionSyntax ea:
                return TranslateElementAccess(ea, env);

            case InvocationExpressionSyntax inv:
                return TranslateInvocation(inv, env);

            case IsPatternExpressionSyntax isPat:
                var subj = TranslateExpression(isPat.Expression, env);
                var patCond = TranslatePattern(subj, isPat.Pattern, env);
                return new SmtTypedTerm(patCond, _compilation.GetSpecialType(SpecialType.System_Boolean));

            case CastExpressionSyntax cast:
                var targetType = ResolveTypeSyntax(cast.Type);
                var inner = TranslateExpression(cast.Expression, env, targetType);
                return ConvertTo(inner, targetType);

            default:
                throw new ContractLoweringException($"Unsupported C# expression syntax '{expr.Kind()}': '{expr}'.");
        }
    }

    private SmtTypedTerm TranslateIdentifier(string name, IReadOnlyDictionary<string, SmtTypedTerm> env)
    {
        if (env.TryGetValue(name, out var bound))
        {
            return bound;
        }

        if (env.TryGetValue("this", out var self))
        {
            return TranslateMemberOnReceiver(self, name);
        }

        throw new ContractLoweringException($"Unknown identifier '{name}' in contract expression.");
    }

    private SmtTypedTerm TranslateLiteral(LiteralExpressionSyntax lit, ITypeSymbol? expectedType)
    {
        switch (lit.Kind())
        {
            case SyntaxKind.TrueLiteralExpression:
                return new SmtTypedTerm("true", _compilation.GetSpecialType(SpecialType.System_Boolean));
            case SyntaxKind.FalseLiteralExpression:
                return new SmtTypedTerm("false", _compilation.GetSpecialType(SpecialType.System_Boolean));
            case SyntaxKind.NullLiteralExpression:
                if (expectedType is not null && _types.TryGetNullableInfo(expectedType, out _, out var optId, out _))
                    return new SmtTypedTerm($"opt-none-{optId}", expectedType);
                return new SmtTypedTerm("str-null", _compilation.GetSpecialType(SpecialType.System_String));
            case SyntaxKind.StringLiteralExpression:
                var str = lit.Token.ValueText;
                return new SmtTypedTerm($"(str-val {EscapeSmtString(str)})", _compilation.GetSpecialType(SpecialType.System_String));
            case SyntaxKind.NumericLiteralExpression:
                var val = lit.Token.Value;
                if (val is int iv)
                    return new SmtTypedTerm(FormatIntLiteral(iv, bitWidth: 32), _compilation.GetSpecialType(SpecialType.System_Int32));
                if (val is long lv)
                    return new SmtTypedTerm(FormatLongLiteral(lv), _compilation.GetSpecialType(SpecialType.System_Int64));
                if (val is decimal dv)
                    return new SmtTypedTerm(FormatDecimalLiteral(dv), _compilation.GetSpecialType(SpecialType.System_Decimal));
                if (val is uint uv)
                    return new SmtTypedTerm(uv.ToString(CultureInfo.InvariantCulture), _compilation.GetSpecialType(SpecialType.System_UInt32));
                if (val is byte bv)
                    return new SmtTypedTerm(bv.ToString(CultureInfo.InvariantCulture), _compilation.GetSpecialType(SpecialType.System_Byte));
                if (val is short sv)
                    return new SmtTypedTerm(sv.ToString(CultureInfo.InvariantCulture), _compilation.GetSpecialType(SpecialType.System_Int16));
                throw new ContractLoweringException($"Unsupported numeric literal '{lit}'.");
            default:
                throw new ContractLoweringException($"Unsupported literal '{lit}'.");
        }
    }

    private SmtTypedTerm TranslatePrefixUnary(PrefixUnaryExpressionSyntax pre, IReadOnlyDictionary<string, SmtTypedTerm> env, ITypeSymbol? expectedType)
    {
        if (pre.IsKind(SyntaxKind.UnaryMinusExpression) && pre.Operand is LiteralExpressionSyntax lit && lit.Token.Value is int iv)
        {
            return new SmtTypedTerm(FormatIntLiteral(-iv, 32), _compilation.GetSpecialType(SpecialType.System_Int32));
        }
        if (pre.IsKind(SyntaxKind.UnaryMinusExpression) && pre.Operand is LiteralExpressionSyntax llit && llit.Token.Value is long lv)
        {
            return new SmtTypedTerm(FormatLongLiteral(-lv), _compilation.GetSpecialType(SpecialType.System_Int64));
        }
        if (pre.IsKind(SyntaxKind.UnaryMinusExpression) && pre.Operand is LiteralExpressionSyntax dlit && dlit.Token.Value is decimal dv)
        {
            return new SmtTypedTerm(FormatDecimalLiteral(-dv), _compilation.GetSpecialType(SpecialType.System_Decimal));
        }

        var operand = UnwrapRefinedIfApplicable(TranslateExpression(pre.Operand, env, expectedType));
        return pre.Kind() switch
        {
            SyntaxKind.LogicalNotExpression => new SmtTypedTerm($"(not {operand.Term})", _compilation.GetSpecialType(SpecialType.System_Boolean)),
            SyntaxKind.UnaryMinusExpression => IsBitVecSort(_types.GetSort(operand.Type))
                ? new SmtTypedTerm($"(bvneg {operand.Term})", operand.Type)
                : new SmtTypedTerm($"(- {operand.Term})", operand.Type),
            SyntaxKind.UnaryPlusExpression => operand,
            _ => throw new ContractLoweringException($"Unsupported unary operator '{pre.Kind()}'.")
        };
    }

    private SmtTypedTerm TranslateBinary(BinaryExpressionSyntax bin, IReadOnlyDictionary<string, SmtTypedTerm> env)
    {
        var kind = bin.Kind();
        if (kind is SyntaxKind.LogicalAndExpression or SyntaxKind.LogicalOrExpression)
        {
            var boolType = _compilation.GetSpecialType(SpecialType.System_Boolean);
            var l = TranslateExpression(bin.Left, env, boolType);
            var r = TranslateExpression(bin.Right, env, boolType);
            var op = kind == SyntaxKind.LogicalAndExpression ? "and" : "or";
            return new SmtTypedTerm($"({op} {l.Term} {r.Term})", boolType);
        }

        var left = TranslateExpression(bin.Left, env);
        var right = TranslateExpression(bin.Right, env, left.Type);

        if (kind is SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression)
        {
            var (lEq, rEq, _) = UnifyForComparison(left, right);
            var eq = $"(= {lEq} {rEq})";
            return new SmtTypedTerm(
                kind == SyntaxKind.EqualsExpression ? eq : $"(not {eq})",
                _compilation.GetSpecialType(SpecialType.System_Boolean));
        }

        var (lTerm, rTerm, unifiedType) = UnifyNumericPair(left, right);
        var sort = _types.GetSort(unifiedType);
        var isBv = IsBitVecSort(sort);
        var boolSortType = _compilation.GetSpecialType(SpecialType.System_Boolean);

        return kind switch
        {
            SyntaxKind.LessThanExpression => new SmtTypedTerm($"({(isBv ? "bvslt" : "<")} {lTerm} {rTerm})", boolSortType),
            SyntaxKind.LessThanOrEqualExpression => new SmtTypedTerm($"({(isBv ? "bvsle" : "<=")} {lTerm} {rTerm})", boolSortType),
            SyntaxKind.GreaterThanExpression => new SmtTypedTerm($"({(isBv ? "bvsgt" : ">")} {lTerm} {rTerm})", boolSortType),
            SyntaxKind.GreaterThanOrEqualExpression => new SmtTypedTerm($"({(isBv ? "bvsge" : ">=")} {lTerm} {rTerm})", boolSortType),
            SyntaxKind.AddExpression => new SmtTypedTerm($"({(isBv ? "bvadd" : "+")} {lTerm} {rTerm})", unifiedType),
            SyntaxKind.SubtractExpression => new SmtTypedTerm($"({(isBv ? "bvsub" : "-")} {lTerm} {rTerm})", unifiedType),
            SyntaxKind.MultiplyExpression => new SmtTypedTerm($"({(isBv ? "bvmul" : "*")} {lTerm} {rTerm})", unifiedType),
            SyntaxKind.DivideExpression => new SmtTypedTerm($"({(isBv ? "bvsdiv" : (sort == "Real" ? "/" : "div"))} {lTerm} {rTerm})", unifiedType),
            SyntaxKind.ModuloExpression => new SmtTypedTerm($"({(isBv ? "bvsrem" : "mod")} {lTerm} {rTerm})", unifiedType),
            _ => throw new ContractLoweringException($"Unsupported binary operator '{kind}'.")
        };
    }

    private SmtTypedTerm TranslateMemberAccess(MemberAccessExpressionSyntax ma, IReadOnlyDictionary<string, SmtTypedTerm> env)
    {
        var memberName = ma.Name.Identifier.ValueText;

        // Check if static type access (e.g., Guid.Empty, TodoStatus.Completed, int.MaxValue)
        if (ma.Expression is IdentifierNameSyntax typeId && !env.ContainsKey(typeId.Identifier.ValueText))
        {
            // Check if it's an implicit member on `this` first
            if (env.TryGetValue("this", out var self) && HasMember(self.Type, typeId.Identifier.ValueText))
            {
                var recv = TranslateMemberOnReceiver(self, typeId.Identifier.ValueText);
                return TranslateMemberOnReceiver(recv, memberName);
            }

            if (TryResolveStaticMember(typeId.Identifier.ValueText, memberName, out var staticTerm))
            {
                return staticTerm!;
            }
        }

        if (TryResolveStaticQualifiedMember(ma, out var qStaticTerm))
        {
            return qStaticTerm!;
        }

        var receiver = TranslateExpression(ma.Expression, env);
        return TranslateMemberOnReceiver(receiver, memberName);
    }

    public SmtTypedTerm TranslateMemberOnReceiver(SmtTypedTerm receiver, string memberName)
    {
        if (receiver.Type.SpecialType == SpecialType.System_String && memberName == "Length")
        {
            return new SmtTypedTerm(
                $"(ite (= {receiver.Term} str-null) 0 (str.len (str-text {receiver.Term})))",
                _compilation.GetSpecialType(SpecialType.System_Int32));
        }

        if (memberName == "Length" &&
            SmtTypeSystem.TryGetRefinedUnderlying(receiver.Type, out var strUnder, out _) &&
            strUnder!.SpecialType == SpecialType.System_String)
        {
            var unwrapped = UnwrapRefinedIfApplicable(receiver);
            return new SmtTypedTerm(
                $"(ite (= {unwrapped.Term} str-null) 0 (str.len (str-text {unwrapped.Term})))",
                _compilation.GetSpecialType(SpecialType.System_Int32));
        }

        if (_types.TryGetDictionaryInfo(receiver.Type, out _, out var dictId, out _, out _) && memberName == "Count")
        {
            return new SmtTypedTerm($"(map_count_{dictId} {receiver.Term})", _compilation.GetSpecialType(SpecialType.System_Int32));
        }

        if (_types.TryGetSequenceInfo(receiver.Type, out _, out var seqId, out _) && memberName is "Count" or "Length")
        {
            return new SmtTypedTerm($"(seq_count_{seqId} {receiver.Term})", _compilation.GetSpecialType(SpecialType.System_Int32));
        }

        if (_types.TryGetSetInfo(receiver.Type, out _, out var setId, out _) && memberName == "Count")
        {
            return new SmtTypedTerm($"(set_count_{setId} {receiver.Term})", _compilation.GetSpecialType(SpecialType.System_Int32));
        }

        if (_types.TryGetNullableInfo(receiver.Type, out _, out var optId, out var innerType))
        {
            if (memberName == "HasValue")
                return new SmtTypedTerm($"(not (= {receiver.Term} opt-none-{optId}))", _compilation.GetSpecialType(SpecialType.System_Boolean));
            if (memberName == "Value")
                return new SmtTypedTerm($"(opt-val-{optId} {receiver.Term})", innerType!);
        }

        if (_types.TryGetComposite(receiver.Type, out var comp))
        {
            var field = comp!.Fields.FirstOrDefault(f => f.Name == memberName);
            if (field is not null)
            {
                return new SmtTypedTerm($"({field.AccessorName} {receiver.Term})", field.Type);
            }
        }

        var prop = receiver.Type.GetMembers(memberName).OfType<IPropertySymbol>().FirstOrDefault();
        if (prop is not null && SmtTypeSystem.TryGetComputedPropertyExpression(prop, out var bodyExpr))
        {
            var propEnv = new Dictionary<string, SmtTypedTerm>(StringComparer.Ordinal)
            {
                ["this"] = receiver
            };
            return TranslateExpression(bodyExpr!, propEnv, prop.Type);
        }

        throw new ContractLoweringException($"Member '{memberName}' not found or unsupported on '{receiver.Type.Name}'.");
    }

    private SmtTypedTerm TranslateElementAccess(ElementAccessExpressionSyntax ea, IReadOnlyDictionary<string, SmtTypedTerm> env)
    {
        if (ea.ArgumentList.Arguments.Count != 1)
        {
            throw new ContractLoweringException("Only single-argument indexers are supported.");
        }

        var recv = TranslateExpression(ea.Expression, env);
        if (_types.TryGetDictionaryInfo(recv.Type, out _, out var dictId, out var keyType, out var valType))
        {
            var key = TranslateExpression(ea.ArgumentList.Arguments[0].Expression, env, keyType);
            var keyConverted = ConvertTo(key, keyType!);
            return new SmtTypedTerm($"(select (map_data_{dictId} {recv.Term}) {keyConverted.Term})", valType!);
        }

        if (_types.TryGetSequenceInfo(recv.Type, out _, out var seqId, out var elemType))
        {
            var idx = TranslateExpression(ea.ArgumentList.Arguments[0].Expression, env, _compilation.GetSpecialType(SpecialType.System_Int32));
            return new SmtTypedTerm($"(select (seq_elems_{seqId} {recv.Term}) {IntTermForIndex(idx)})", elemType!);
        }

        throw new ContractLoweringException($"Unsupported indexer access on '{recv.Type.Name}'.");
    }

    private SmtTypedTerm TranslateInvocation(InvocationExpressionSyntax inv, IReadOnlyDictionary<string, SmtTypedTerm> env)
    {
        if (inv.Expression is MemberAccessExpressionSyntax ma)
        {
            var methodName = ma.Name.Identifier.ValueText;

            if (ma.Expression is IdentifierNameSyntax id && !env.ContainsKey(id.Identifier.ValueText))
            {
                // Static call like Math.Abs(x), string.IsNullOrEmpty(s), TodoId.From(x)
                var args = inv.ArgumentList.Arguments.Select(a => TranslateExpression(a.Expression, env)).ToList();
                if (id.Identifier.ValueText is "Math" or "decimal" or "Decimal")
                {
                    if (methodName == "Abs" && args.Count == 1)
                    {
                        var zero = _types.GetSort(args[0].Type) == "Real" ? "0.0" : FormatIntLiteral(0, 32);
                        var ltOp = IsBitVecSort(_types.GetSort(args[0].Type)) ? "bvslt" : "<";
                        var negOp = IsBitVecSort(_types.GetSort(args[0].Type)) ? "bvneg" : "-";
                        return new SmtTypedTerm($"(ite ({ltOp} {args[0].Term} {zero}) ({negOp} {args[0].Term}) {args[0].Term})", args[0].Type);
                    }
                    if (methodName == "Max" && args.Count == 2)
                    {
                        var (a, b, t) = UnifyNumericPair(args[0], args[1]);
                        var geOp = IsBitVecSort(_types.GetSort(t)) ? "bvsge" : ">=";
                        return new SmtTypedTerm($"(ite ({geOp} {a} {b}) {a} {b})", t);
                    }
                    if (methodName == "Min" && args.Count == 2)
                    {
                        var (a, b, t) = UnifyNumericPair(args[0], args[1]);
                        var leOp = IsBitVecSort(_types.GetSort(t)) ? "bvsle" : "<=";
                        return new SmtTypedTerm($"(ite ({leOp} {a} {b}) {a} {b})", t);
                    }
                }

                if (id.Identifier.ValueText is "string" or "String" && methodName == "IsNullOrEmpty" && args.Count == 1)
                {
                    return new SmtTypedTerm(
                        $"(or (= {args[0].Term} str-null) (= (str.len (str-text {args[0].Term})) 0))",
                        _compilation.GetSpecialType(SpecialType.System_Boolean));
                }

                var resolvedType = TryResolveNamedType(id.Identifier.ValueText);
                if (resolvedType is not null && methodName == "From" && args.Count == 1 &&
                    SmtTypeSystem.TryGetRefinedUnderlying(resolvedType, out var under, out _) &&
                    _types.TryGetComposite(resolvedType, out var comp))
                {
                    var converted = ConvertTo(args[0], under!);
                    return new SmtTypedTerm($"({comp!.ConstructorName} {converted.Term})", resolvedType);
                }
            }

            var recv = TranslateExpression(ma.Expression, env);
            if (_types.TryGetDictionaryInfo(recv.Type, out _, out var dictId, out var keyType, out _) &&
                methodName == "ContainsKey" &&
                inv.ArgumentList.Arguments.Count == 1)
            {
                var k = ConvertTo(TranslateExpression(inv.ArgumentList.Arguments[0].Expression, env, keyType), keyType!);
                return new SmtTypedTerm($"(select (map_has_{dictId} {recv.Term}) {k.Term})", _compilation.GetSpecialType(SpecialType.System_Boolean));
            }

            if (_types.TryGetSetInfo(recv.Type, out _, out var setId, out var setElemType) &&
                methodName == "Contains" &&
                inv.ArgumentList.Arguments.Count == 1)
            {
                var elem = ConvertTo(TranslateExpression(inv.ArgumentList.Arguments[0].Expression, env, setElemType), setElemType!);
                return new SmtTypedTerm($"(select (set_has_{setId} {recv.Term}) {elem.Term})", _compilation.GetSpecialType(SpecialType.System_Boolean));
            }

            if (methodName == "Equals" && inv.ArgumentList.Arguments.Count == 1)
            {
                var other = TranslateExpression(inv.ArgumentList.Arguments[0].Expression, env, recv.Type);
                var (l, r, _) = UnifyForComparison(recv, other);
                return new SmtTypedTerm($"(= {l} {r})", _compilation.GetSpecialType(SpecialType.System_Boolean));
            }
        }

        throw new ContractLoweringException($"Unsupported invocation '{inv}' in contract expression.");
    }

    private string TranslatePattern(SmtTypedTerm subject, PatternSyntax pattern, IReadOnlyDictionary<string, SmtTypedTerm> env)
    {
        switch (pattern)
        {
            case ParenthesizedPatternSyntax paren:
                return TranslatePattern(subject, paren.Pattern, env);

            case UnaryPatternSyntax unary when unary.OperatorToken.IsKind(SyntaxKind.NotKeyword):
                return $"(not {TranslatePattern(subject, unary.Pattern, env)})";

            case BinaryPatternSyntax bin:
                var left = TranslatePattern(subject, bin.Left, env);
                var right = TranslatePattern(subject, bin.Right, env);
                return bin.IsKind(SyntaxKind.AndPattern) ? $"(and {left} {right})" : $"(or {left} {right})";

            case RelationalPatternSyntax rel:
                var rhs = TranslateExpression(rel.Expression, env, subject.Type);
                var (lTerm, rTerm, unified) = UnifyNumericPair(subject, rhs);
                var isBv = IsBitVecSort(_types.GetSort(unified));
                var op = rel.OperatorToken.Kind() switch
                {
                    SyntaxKind.LessThanToken => isBv ? "bvslt" : "<",
                    SyntaxKind.LessThanEqualsToken => isBv ? "bvsle" : "<=",
                    SyntaxKind.GreaterThanToken => isBv ? "bvsgt" : ">",
                    SyntaxKind.GreaterThanEqualsToken => isBv ? "bvsge" : ">=",
                    _ => throw new ContractLoweringException($"Unsupported relational pattern '{rel}'.")
                };
                return $"({op} {lTerm} {rTerm})";

            case ConstantPatternSyntax cp:
                var val = TranslateExpression(cp.Expression, env, subject.Type);
                var (lc, rc, _) = UnifyForComparison(subject, val);
                return $"(= {lc} {rc})";

            default:
                throw new ContractLoweringException($"Unsupported pattern '{pattern}' in contract expression.");
        }
    }

    private bool TryResolveStaticMember(string typeName, string memberName, out SmtTypedTerm? term)
    {
        if (typeName == "Guid" && memberName == "Empty")
        {
            var guidType = _compilation.GetTypeByMetadataName("System.Guid")!;
            term = new SmtTypedTerm("0", guidType);
            return true;
        }

        if (typeName == "int" && memberName is "MaxValue" or "MinValue")
        {
            var v = memberName == "MaxValue" ? int.MaxValue : int.MinValue;
            term = new SmtTypedTerm(FormatIntLiteral(v, 32), _compilation.GetSpecialType(SpecialType.System_Int32));
            return true;
        }

        if (typeName == "long" && memberName is "MaxValue" or "MinValue")
        {
            var v = memberName == "MaxValue" ? long.MaxValue : long.MinValue;
            term = new SmtTypedTerm(FormatLongLiteral(v), _compilation.GetSpecialType(SpecialType.System_Int64));
            return true;
        }

        if (typeName == "string" && memberName == "Empty")
        {
            term = new SmtTypedTerm("(str-val \"\")", _compilation.GetSpecialType(SpecialType.System_String));
            return true;
        }

        var named = TryResolveNamedType(typeName);
        if (named is not null)
        {
            if (named.TypeKind == TypeKind.Enum)
            {
                var field = named.GetMembers(memberName).OfType<IFieldSymbol>().FirstOrDefault(f => f.HasConstantValue);
                if (field?.ConstantValue is not null)
                {
                    var num = Convert.ToInt64(field.ConstantValue, CultureInfo.InvariantCulture);
                    term = new SmtTypedTerm(num < 0 ? $"(- {-num})" : num.ToString(CultureInfo.InvariantCulture), named);
                    return true;
                }
            }
        }

        term = null;
        return false;
    }

    private bool TryResolveStaticQualifiedMember(MemberAccessExpressionSyntax ma, out SmtTypedTerm? term)
    {
        var fullText = ma.ToString();
        if (fullText == "System.Guid.Empty")
        {
            term = new SmtTypedTerm("0", _compilation.GetTypeByMetadataName("System.Guid")!);
            return true;
        }

        if (ma.Expression is MemberAccessExpressionSyntax qualifier)
        {
            var nsType = _compilation.GetTypeByMetadataName(qualifier.ToString());
            if (nsType is { TypeKind: TypeKind.Enum })
            {
                var field = nsType.GetMembers(ma.Name.Identifier.ValueText).OfType<IFieldSymbol>().FirstOrDefault(f => f.HasConstantValue);
                if (field?.ConstantValue is not null)
                {
                    var num = Convert.ToInt64(field.ConstantValue, CultureInfo.InvariantCulture);
                    term = new SmtTypedTerm(num < 0 ? $"(- {-num})" : num.ToString(CultureInfo.InvariantCulture), nsType);
                    return true;
                }
            }
        }

        term = null;
        return false;
    }

    private INamedTypeSymbol? TryResolveNamedType(string name)
    {
        if (name == "Guid") return _compilation.GetTypeByMetadataName("System.Guid");
        if (name == "Math") return _compilation.GetTypeByMetadataName("System.Math");

        for (var cur = _enclosingType; cur is not null; cur = cur.ContainingType)
        {
            if (cur.Name == name) return cur;
            var nested = cur.GetTypeMembers(name).FirstOrDefault();
            if (nested is not null) return nested;
        }

        if (_enclosingType?.ContainingNamespace is { } ns)
        {
            var sameNs = ns.GetTypeMembers(name).FirstOrDefault();
            if (sameNs is not null) return sameNs;
        }

        foreach (var u in _usingNamespaces)
        {
            var found = _compilation.GetTypeByMetadataName($"{u}.{name}");
            if (found is not null) return found;
        }

        return _compilation.GetTypeByMetadataName(name);
    }

    private ITypeSymbol ResolveTypeSyntax(TypeSyntax typeSyntax)
    {
        var text = typeSyntax.ToString();
        return text switch
        {
            "int" => _compilation.GetSpecialType(SpecialType.System_Int32),
            "long" => _compilation.GetSpecialType(SpecialType.System_Int64),
            "decimal" => _compilation.GetSpecialType(SpecialType.System_Decimal),
            "bool" => _compilation.GetSpecialType(SpecialType.System_Boolean),
            "string" => _compilation.GetSpecialType(SpecialType.System_String),
            _ => TryResolveNamedType(text) ?? throw new ContractLoweringException($"Cannot resolve cast target type '{text}'.")
        };
    }

    public SmtTypedTerm UnwrapRefinedIfApplicable(SmtTypedTerm term)
    {
        if (SmtTypeSystem.TryGetRefinedUnderlying(term.Type, out var underlying, out _) &&
            _types.TryGetComposite(term.Type, out var comp) &&
            comp!.Fields.Count == 1)
        {
            return new SmtTypedTerm($"({comp.Fields[0].AccessorName} {term.Term})", underlying!);
        }

        return term;
    }

    public SmtTypedTerm ConvertTo(SmtTypedTerm value, ITypeSymbol targetType)
    {
        var srcSort = _types.GetSort(value.Type);
        var dstSort = _types.GetSort(targetType);
        if (srcSort == dstSort)
        {
            return new SmtTypedTerm(value.Term, targetType);
        }

        if (_types.TryGetNullableInfo(targetType, out _, out var optId, out var innerType))
        {
            if (value.Term == "str-null" || value.Term.StartsWith("opt-none-", StringComparison.Ordinal))
            {
                return new SmtTypedTerm($"opt-none-{optId}", targetType);
            }
            var inner = ConvertTo(value, innerType!);
            return new SmtTypedTerm($"(opt-some-{optId} {inner.Term})", targetType);
        }

        if (SmtTypeSystem.TryGetRefinedUnderlying(value.Type, out _, out _))
        {
            var unwrapped = UnwrapRefinedIfApplicable(value);
            if (!SymbolEqualityComparer.Default.Equals(unwrapped.Type, value.Type))
            {
                return ConvertTo(unwrapped, targetType);
            }
        }

        if (dstSort == "Real")
        {
            if (srcSort == "Int")
            {
                return new SmtTypedTerm($"(to_real {value.Term})", targetType);
            }
            if (srcSort == "(_ BitVec 32)")
            {
                return new SmtTypedTerm($"(to_real {BitVecToInt(value.Term, 32)})", targetType);
            }
            if (srcSort == "(_ BitVec 64)")
            {
                return new SmtTypedTerm($"(to_real {BitVecToInt(value.Term, 64)})", targetType);
            }
        }

        if (dstSort == "Int")
        {
            if (srcSort == "(_ BitVec 32)")
                return new SmtTypedTerm(BitVecToInt(value.Term, 32), targetType);
            if (srcSort == "(_ BitVec 64)")
                return new SmtTypedTerm(BitVecToInt(value.Term, 64), targetType);
            if (srcSort == "Real")
                return new SmtTypedTerm($"(to_int {value.Term})", targetType);
        }

        if (dstSort == "(_ BitVec 64)" && srcSort == "(_ BitVec 32)")
        {
            return new SmtTypedTerm($"((_ sign_extend 32) {value.Term})", targetType);
        }

        return new SmtTypedTerm(value.Term, targetType);
    }

    public (string Left, string Right, ITypeSymbol UnifiedType) UnifyNumericPair(SmtTypedTerm left, SmtTypedTerm right)
    {
        left = UnwrapRefinedIfApplicable(left);
        right = UnwrapRefinedIfApplicable(right);

        var ls = _types.GetSort(left.Type);
        var rs = _types.GetSort(right.Type);
        if (ls == rs)
        {
            return (left.Term, right.Term, left.Type);
        }

        if (ls == "Real" || rs == "Real")
        {
            var decType = _compilation.GetSpecialType(SpecialType.System_Decimal);
            return (ConvertTo(left, decType).Term, ConvertTo(right, decType).Term, decType);
        }

        if (ls == "(_ BitVec 64)" || rs == "(_ BitVec 64)")
        {
            var lType = _compilation.GetSpecialType(SpecialType.System_Int64);
            return (ConvertTo(left, lType).Term, ConvertTo(right, lType).Term, lType);
        }

        if (ls == "(_ BitVec 32)" || rs == "(_ BitVec 32)")
        {
            if (ls == "Int") return (left.Term, BitVecToInt(right.Term, 32), left.Type);
            if (rs == "Int") return (BitVecToInt(left.Term, 32), right.Term, right.Type);
        }

        return (left.Term, right.Term, left.Type);
    }

    public (string Left, string Right, ITypeSymbol UnifiedType) UnifyForComparison(SmtTypedTerm left, SmtTypedTerm right)
    {
        var ls = _types.GetSort(left.Type);
        var rs = _types.GetSort(right.Type);
        if (ls == rs) return (left.Term, right.Term, left.Type);

        if (_types.TryGetNullableInfo(left.Type, out _, out var lOptId, out _))
        {
            if (right.Term == "str-null" || right.Term.StartsWith("opt-none-", StringComparison.Ordinal))
                return (left.Term, $"opt-none-{lOptId}", left.Type);
            return (left.Term, ConvertTo(right, left.Type).Term, left.Type);
        }

        if (_types.TryGetNullableInfo(right.Type, out _, out var rOptId, out _))
        {
            if (left.Term == "str-null" || left.Term.StartsWith("opt-none-", StringComparison.Ordinal))
                return ($"opt-none-{rOptId}", right.Term, right.Type);
            return (ConvertTo(left, right.Type).Term, right.Term, right.Type);
        }

        var unwrappedLeft = UnwrapRefinedIfApplicable(left);
        var unwrappedRight = UnwrapRefinedIfApplicable(right);
        if (!SymbolEqualityComparer.Default.Equals(unwrappedLeft.Type, left.Type) ||
            !SymbolEqualityComparer.Default.Equals(unwrappedRight.Type, right.Type))
        {
            return UnifyForComparison(unwrappedLeft, unwrappedRight);
        }

        return UnifyNumericPair(left, right);
    }

    public string IntTermForIndex(SmtTypedTerm idx)
    {
        var sort = _types.GetSort(idx.Type);
        return sort switch
        {
            "(_ BitVec 32)" => BitVecToInt(idx.Term, 32),
            "(_ BitVec 64)" => BitVecToInt(idx.Term, 64),
            _ => idx.Term
        };
    }

    public string FormatIntLiteral(int val, int bitWidth)
    {
        if (!_types.UseBitVec)
        {
            return val < 0 ? $"(- {-((long)val)})" : val.ToString(CultureInfo.InvariantCulture);
        }

        if (bitWidth == 32)
        {
            var u = unchecked((uint)val);
            return $"(_ bv{u.ToString(CultureInfo.InvariantCulture)} 32)";
        }

        var ul = unchecked((ulong)(long)val);
        return $"(_ bv{ul.ToString(CultureInfo.InvariantCulture)} 64)";
    }

    public string FormatLongLiteral(long val)
    {
        if (!_types.UseBitVec)
        {
            if (val == long.MinValue) return "(- 9223372036854775808)";
            return val < 0 ? $"(- {(-val).ToString(CultureInfo.InvariantCulture)})" : val.ToString(CultureInfo.InvariantCulture);
        }

        var ul = unchecked((ulong)val);
        return $"(_ bv{ul.ToString(CultureInfo.InvariantCulture)} 64)";
    }

    public static string FormatDecimalLiteral(decimal value)
    {
        if (value == 0m) return "0.0";
        var bits = decimal.GetBits(value);
        var lo = (uint)bits[0];
        var mid = (uint)bits[1];
        var hi = (uint)bits[2];
        var flags = bits[3];
        var isNeg = (flags & unchecked((int)0x80000000)) != 0;
        var scale = (flags >> 16) & 0x7F;

        var mantissa = ((BigInteger)hi << 64) | ((BigInteger)mid << 32) | lo;
        string term;
        if (scale == 0)
        {
            term = mantissa.ToString(CultureInfo.InvariantCulture) + ".0";
        }
        else
        {
            var denom = BigInteger.Pow(10, scale);
            term = $"(/ {mantissa.ToString(CultureInfo.InvariantCulture)} {denom.ToString(CultureInfo.InvariantCulture)})";
        }

        return isNeg ? $"(- {term})" : term;
    }

    public static string EscapeSmtString(string text) =>
        "\"" + text.Replace("\"", "\"\"") + "\"";

    public static bool IsBitVecSort(string sort) =>
        sort is "(_ BitVec 32)" or "(_ BitVec 64)";

    private static string BitVecToInt(string bvTerm, int width)
    {
        var zero = $"(_ bv0 {width})";
        return $"(ite (bvslt {bvTerm} {zero}) (- (bv2int (bvneg {bvTerm}))) (bv2int {bvTerm}))";
    }

    private static bool HasMember(ITypeSymbol type, string name) =>
        type.GetMembers(name).Length > 0;

    private static IReadOnlyList<string> CollectUsings(SyntaxTree? tree)
    {
        var list = new List<string>();
        if (tree?.GetRoot() is CompilationUnitSyntax cu)
        {
            foreach (var u in cu.Usings)
            {
                if (u.Alias is null && u.Name is not null)
                {
                    list.Add(u.Name.ToString());
                }
            }
        }
        return list;
    }
}
