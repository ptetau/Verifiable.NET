using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Verifiable.Generators;

/// <summary>
/// Rewrites a pure C# expression from a compiling assembly into a standalone, <c>global::</c>-qualified
/// C# expression string suitable for serialization in <c>[assembly: VerifiableContractMetadata(...)]</c>.
/// Fails closed if the expression references any inaccessible non-constant symbol.
/// </summary>
internal sealed class ContractExpressionCanonicalizer : CSharpSyntaxRewriter
{
    public const string ThisPlaceholder = "__verifiable_this";
    public const string ResultPlaceholder = "__verifiable_result";

    private readonly SemanticModel _model;
    private readonly IAssemblySymbol _sourceAssembly;
    private readonly bool _allowInternal;
    private readonly IParameterSymbol? _resultParam;
    private bool _failed;

    private ContractExpressionCanonicalizer(
        SemanticModel model,
        IAssemblySymbol sourceAssembly,
        bool allowInternal,
        IParameterSymbol? resultParam)
    {
        _model = model;
        _sourceAssembly = sourceAssembly;
        _allowInternal = allowInternal;
        _resultParam = resultParam;
    }

    public static bool TryCanonicalize(
        SemanticModel model,
        ExpressionSyntax expression,
        bool allowInternal,
        out string canonicalExpression,
        IParameterSymbol? resultParam = null)
    {
        var rewriter = new ContractExpressionCanonicalizer(
            model,
            model.Compilation.Assembly,
            allowInternal,
            resultParam);
        var rewritten = rewriter.Visit(expression);
        if (rewriter._failed || rewritten is null)
        {
            canonicalExpression = string.Empty;
            return false;
        }

        canonicalExpression = rewritten.NormalizeWhitespace().ToFullString();
        return !string.IsNullOrWhiteSpace(canonicalExpression);
    }

    public override SyntaxNode? Visit(SyntaxNode? node) =>
        _failed || node is null ? node : base.Visit(node);

    public override SyntaxNode? VisitThisExpression(ThisExpressionSyntax node) =>
        SyntaxFactory.IdentifierName(ThisPlaceholder).WithTriviaFrom(node);

    public override SyntaxNode? VisitBaseExpression(BaseExpressionSyntax node)
    {
        _failed = true;
        return node;
    }

    public override SyntaxNode? VisitNameColon(NameColonSyntax node) => node;

    public override SyntaxNode? VisitExpressionColon(ExpressionColonSyntax node) => node;

    public override SyntaxNode? VisitLiteralExpression(LiteralExpressionSyntax node)
    {
        if (node.IsKind(SyntaxKind.DefaultLiteralExpression))
        {
            var targetType = _model.GetTypeInfo(node).ConvertedType ?? _model.GetTypeInfo(node).Type;
            if (targetType is null || !IsTypeAccessible(targetType))
            {
                _failed = true;
                return node;
            }

            return SyntaxFactory.DefaultExpression(ParseGlobalType(targetType)).WithTriviaFrom(node);
        }

        return base.VisitLiteralExpression(node);
    }

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        // Rewrite nameof(...) to its constant string literal.
        if (node.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" })
        {
            var constVal = _model.GetConstantValue(node);
            if (constVal.HasValue && constVal.Value is string nameStr)
            {
                return SyntaxFactory.ParseExpression(TypeRefHelper.EscapeStringLiteral(nameStr)).WithTriviaFrom(node);
            }
        }

        var symbol = _model.GetSymbolInfo(node).Symbol as IMethodSymbol;
        if (symbol is null)
        {
            _failed = true;
            return node;
        }

        // Rewrite Spec.Result<T>() -> __verifiable_result
        if (symbol.ContainingType?.ToDisplayString() == "Verifiable.Contracts.Spec" &&
            symbol.Name == "Result")
        {
            return SyntaxFactory.IdentifierName(ResultPlaceholder).WithTriviaFrom(node);
        }

        // Rewrite private/local [GeneratedRegex] factory call: MyRegex().IsMatch(input)
        // into static global::System.Text.RegularExpressions.Regex.IsMatch(input, pattern, options)
        if (symbol.Name == "IsMatch" &&
            !symbol.IsStatic &&
            symbol.ContainingType?.OriginalDefinition.ToDisplayString() == "System.Text.RegularExpressions.Regex" &&
            node.Expression is MemberAccessExpressionSyntax regexMa &&
            TryExtractRegexConstant(regexMa.Expression, out var regexPattern, out var regexOptions) &&
            node.ArgumentList.Arguments.Count == 1)
        {
            var inputArg = (ExpressionSyntax)Visit(node.ArgumentList.Arguments[0].Expression)!;
            if (_failed) return node;
            var rewrittenRegexCall =
                $"global::System.Text.RegularExpressions.Regex.IsMatch({inputArg.NormalizeWhitespace().ToFullString()}, " +
                $"{TypeRefHelper.EscapeStringLiteral(regexPattern)}, " +
                $"(global::System.Text.RegularExpressions.RegexOptions){regexOptions.ToString(CultureInfo.InvariantCulture)})";
            return SyntaxFactory.ParseExpression(rewrittenRegexCall).WithTriviaFrom(node);
        }

        // Extension method invocation: receiver.Ext(args...) -> global::ExtType.Ext<TArgs>(receiver, args...)
        if (symbol.ReducedFrom is not null && node.Expression is MemberAccessExpressionSyntax extMa)
        {
            if (!IsSymbolAccessible(symbol.ReducedFrom))
            {
                _failed = true;
                return node;
            }

            var recv = (ExpressionSyntax)Visit(extMa.Expression)!;
            var args = RewriteArgumentList(node.ArgumentList);
            if (_failed) return node;

            var allArgs = new List<ArgumentSyntax>(args.Arguments.Count + 1)
            {
                SyntaxFactory.Argument(recv)
            };
            allArgs.AddRange(args.Arguments);

            var qualifiedCallee = BuildStaticMethodExpression(symbol);
            return SyntaxFactory.InvocationExpression(
                qualifiedCallee,
                SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(allArgs)))
                .WithTriviaFrom(node);
        }

        // Ordinary static method invocation: qualify with global::ContainingType.Method<TArgs>
        if (symbol.IsStatic && symbol.MethodKind == MethodKind.Ordinary)
        {
            if (!IsSymbolAccessible(symbol))
            {
                _failed = true;
                return node;
            }

            var args = RewriteArgumentList(node.ArgumentList);
            if (_failed) return node;

            var qualifiedCallee = BuildStaticMethodExpression(symbol);
            return SyntaxFactory.InvocationExpression(qualifiedCallee, args).WithTriviaFrom(node);
        }

        // Instance method invocation
        if (!symbol.IsStatic && symbol.MethodKind == MethodKind.Ordinary)
        {
            if (!IsSymbolAccessible(symbol))
            {
                _failed = true;
                return node;
            }

            var args = RewriteArgumentList(node.ArgumentList);
            if (_failed) return node;

            if (node.Expression is SimpleNameSyntax)
            {
                var memberName = BuildSimpleMethodName(symbol);
                var access = SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName(ThisPlaceholder),
                    memberName);
                return SyntaxFactory.InvocationExpression(access, args).WithTriviaFrom(node);
            }

            if (node.Expression is MemberAccessExpressionSyntax instMa)
            {
                var recv = (ExpressionSyntax)Visit(instMa.Expression)!;
                if (_failed) return node;
                var memberName = BuildSimpleMethodName(symbol);
                var access = SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    recv,
                    memberName);
                return SyntaxFactory.InvocationExpression(access, args).WithTriviaFrom(node);
            }
        }

        // Delegate invocation or other invocation
        return base.VisitInvocationExpression(node);
    }

    public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        var symbol = _model.GetSymbolInfo(node).Symbol;
        if (symbol is ITypeSymbol typeSym)
        {
            if (!IsTypeAccessible(typeSym))
            {
                _failed = true;
                return node;
            }

            return ParseGlobalType(typeSym).WithTriviaFrom(node);
        }

        if (symbol is IFieldSymbol { IsConst: true } constField)
        {
            if (!IsSymbolAccessible(constField))
            {
                return TryFormatConstant(constField.ConstantValue, constField.Type, out var lit)
                    ? SyntaxFactory.ParseExpression(lit).WithTriviaFrom(node)
                    : Fail(node);
            }

            return SyntaxFactory.ParseExpression(
                $"{TypeRefHelper.ToGlobalTypeName(constField.ContainingType)}.{EscapeIdentifier(constField.Name)}")
                .WithTriviaFrom(node);
        }

        if (symbol is IPropertySymbol or IFieldSymbol or IEventSymbol)
        {
            if (symbol.IsStatic)
            {
                if (!IsSymbolAccessible(symbol))
                {
                    _failed = true;
                    return node;
                }

                return SyntaxFactory.ParseExpression(
                    $"{TypeRefHelper.ToGlobalTypeName(symbol.ContainingType)}.{EscapeIdentifier(symbol.Name)}")
                    .WithTriviaFrom(node);
            }

            if (!IsSymbolAccessible(symbol))
            {
                _failed = true;
                return node;
            }

            var recv = (ExpressionSyntax)Visit(node.Expression)!;
            if (_failed) return node;
            return node.WithExpression(recv);
        }

        return base.VisitMemberAccessExpression(node);
    }

    public override SyntaxNode? VisitQualifiedName(QualifiedNameSyntax node)
    {
        var symbol = _model.GetSymbolInfo(node).Symbol;
        if (symbol is ITypeSymbol typeSym)
        {
            if (!IsTypeAccessible(typeSym))
            {
                _failed = true;
                return node;
            }

            return ParseGlobalType(typeSym).WithTriviaFrom(node);
        }

        return base.VisitQualifiedName(node);
    }

    public override SyntaxNode? VisitGenericName(GenericNameSyntax node)
    {
        var symbol = _model.GetSymbolInfo(node).Symbol;
        if (symbol is ITypeSymbol typeSym)
        {
            if (!IsTypeAccessible(typeSym))
            {
                _failed = true;
                return node;
            }

            return ParseGlobalType(typeSym).WithTriviaFrom(node);
        }

        return base.VisitGenericName(node);
    }

    public override SyntaxNode? VisitAliasQualifiedName(AliasQualifiedNameSyntax node)
    {
        var symbol = _model.GetSymbolInfo(node).Symbol;
        if (symbol is ITypeSymbol typeSym)
        {
            if (!IsTypeAccessible(typeSym))
            {
                _failed = true;
                return node;
            }

            return ParseGlobalType(typeSym).WithTriviaFrom(node);
        }

        return base.VisitAliasQualifiedName(node);
    }

    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
    {
        var symbol = _model.GetSymbolInfo(node).Symbol;
        if (symbol is null)
        {
            return node;
        }

        if (symbol is IParameterSymbol paramSym)
        {
            if (_resultParam is not null && SymbolEqualityComparer.Default.Equals(paramSym, _resultParam))
            {
                return SyntaxFactory.IdentifierName(ResultPlaceholder).WithTriviaFrom(node);
            }

            return node;
        }

        if (symbol is ILocalSymbol or IRangeVariableSymbol or ITypeParameterSymbol)
        {
            return node;
        }

        if (symbol is ITypeSymbol typeSym)
        {
            if (!IsTypeAccessible(typeSym))
            {
                _failed = true;
                return node;
            }

            return ParseGlobalType(typeSym).WithTriviaFrom(node);
        }

        if (symbol is IFieldSymbol { IsConst: true } constField)
        {
            if (!IsSymbolAccessible(constField))
            {
                return TryFormatConstant(constField.ConstantValue, constField.Type, out var lit)
                    ? SyntaxFactory.ParseExpression(lit).WithTriviaFrom(node)
                    : Fail(node);
            }

            return SyntaxFactory.ParseExpression(
                $"{TypeRefHelper.ToGlobalTypeName(constField.ContainingType)}.{EscapeIdentifier(constField.Name)}")
                .WithTriviaFrom(node);
        }

        if (symbol is IPropertySymbol or IFieldSymbol)
        {
            if (!IsSymbolAccessible(symbol))
            {
                _failed = true;
                return node;
            }

            if (symbol.IsStatic)
            {
                return SyntaxFactory.ParseExpression(
                    $"{TypeRefHelper.ToGlobalTypeName(symbol.ContainingType)}.{EscapeIdentifier(symbol.Name)}")
                    .WithTriviaFrom(node);
            }

            return SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName(ThisPlaceholder),
                SyntaxFactory.IdentifierName(EscapeIdentifier(symbol.Name)))
                .WithTriviaFrom(node);
        }

        return node;
    }

    public override SyntaxNode? VisitImplicitObjectCreationExpression(ImplicitObjectCreationExpressionSyntax node)
    {
        var type = _model.GetTypeInfo(node).ConvertedType ?? _model.GetTypeInfo(node).Type;
        var ctor = _model.GetSymbolInfo(node).Symbol as IMethodSymbol;
        if (type is null || !IsTypeAccessible(type) || (ctor is not null && !IsSymbolAccessible(ctor)))
        {
            _failed = true;
            return node;
        }

        var args = RewriteArgumentList(node.ArgumentList);
        var init = node.Initializer is not null ? (InitializerExpressionSyntax?)Visit(node.Initializer) : null;
        if (_failed) return node;

        return SyntaxFactory.ObjectCreationExpression(ParseGlobalType(type), args, init).WithTriviaFrom(node);
    }

    public override SyntaxNode? VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        var type = _model.GetTypeInfo(node).Type;
        var ctor = _model.GetSymbolInfo(node).Symbol as IMethodSymbol;
        if (type is null || !IsTypeAccessible(type) || (ctor is not null && !IsSymbolAccessible(ctor)))
        {
            _failed = true;
            return node;
        }

        var args = node.ArgumentList is not null ? RewriteArgumentList(node.ArgumentList) : null;
        var init = node.Initializer is not null ? (InitializerExpressionSyntax?)Visit(node.Initializer) : null;
        if (_failed) return node;

        return SyntaxFactory.ObjectCreationExpression(ParseGlobalType(type), args, init).WithTriviaFrom(node);
    }

    public override SyntaxNode? VisitInitializerExpression(InitializerExpressionSyntax node)
    {
        if (node.IsKind(SyntaxKind.ObjectInitializerExpression) || node.IsKind(SyntaxKind.WithInitializerExpression))
        {
            var rewritten = new List<ExpressionSyntax>(node.Expressions.Count);
            foreach (var expr in node.Expressions)
            {
                if (expr is AssignmentExpressionSyntax assign)
                {
                    var leftSym = _model.GetSymbolInfo(assign.Left).Symbol;
                    if (leftSym is not null && !IsSymbolAccessible(leftSym))
                    {
                        _failed = true;
                        return node;
                    }

                    var right = (ExpressionSyntax)Visit(assign.Right)!;
                    if (_failed) return node;
                    rewritten.Add(assign.WithRight(right));
                }
                else
                {
                    rewritten.Add((ExpressionSyntax)Visit(expr)!);
                    if (_failed) return node;
                }
            }

            return node.WithExpressions(SyntaxFactory.SeparatedList(rewritten));
        }

        return base.VisitInitializerExpression(node);
    }

    private ArgumentListSyntax RewriteArgumentList(ArgumentListSyntax argList)
    {
        var newArgs = new List<ArgumentSyntax>(argList.Arguments.Count);
        foreach (var arg in argList.Arguments)
        {
            var expr = (ExpressionSyntax)Visit(arg.Expression)!;
            if (_failed) return argList;
            newArgs.Add(arg.WithExpression(expr));
        }

        return argList.WithArguments(SyntaxFactory.SeparatedList(newArgs));
    }

    private ExpressionSyntax BuildStaticMethodExpression(IMethodSymbol method)
    {
        var target = method.ReducedFrom ?? method;
        var typeName = TypeRefHelper.ToGlobalTypeName(target.ContainingType);
        var simple = BuildSimpleMethodName(method);
        return SyntaxFactory.ParseExpression($"{typeName}.{simple.ToFullString()}");
    }

    private SimpleNameSyntax BuildSimpleMethodName(IMethodSymbol method)
    {
        var name = EscapeIdentifier(method.Name);
        if (method.TypeArguments.Length == 0)
        {
            return SyntaxFactory.IdentifierName(name);
        }

        var typeArgs = new List<TypeSyntax>(method.TypeArguments.Length);
        foreach (var ta in method.TypeArguments)
        {
            if (!IsTypeAccessible(ta))
            {
                _failed = true;
                return SyntaxFactory.IdentifierName(name);
            }

            typeArgs.Add(ParseGlobalType(ta));
        }

        return SyntaxFactory.GenericName(
            SyntaxFactory.Identifier(name),
            SyntaxFactory.TypeArgumentList(SyntaxFactory.SeparatedList(typeArgs)));
    }

    private bool TryExtractRegexConstant(ExpressionSyntax expr, out string pattern, out int options)
    {
        pattern = string.Empty;
        options = 0;

        if (expr is InvocationExpressionSyntax inv &&
            _model.GetSymbolInfo(inv).Symbol is IMethodSymbol factory)
        {
            foreach (var attr in factory.GetAttributes())
            {
                if (attr.AttributeClass?.ToDisplayString() == TypeRefHelper.GeneratedRegexAttributeMetadataName &&
                    attr.ConstructorArguments.Length >= 1 &&
                    attr.ConstructorArguments[0].Value is string pat)
                {
                    pattern = pat;
                    if (attr.ConstructorArguments.Length >= 2 && attr.ConstructorArguments[1].Value is not null)
                    {
                        options = Convert.ToInt32(attr.ConstructorArguments[1].Value, CultureInfo.InvariantCulture);
                    }
                    return true;
                }
            }
        }

        return false;
    }

    private bool IsSymbolAccessible(ISymbol symbol)
    {
        if (symbol is ITypeSymbol typeSym)
        {
            return IsTypeAccessible(typeSym);
        }

        if (!IsAccessibilityExportable(symbol.DeclaredAccessibility, symbol.ContainingAssembly))
        {
            return false;
        }

        if (symbol.ContainingType is not null && !IsTypeAccessible(symbol.ContainingType))
        {
            return false;
        }

        if (symbol is IPropertySymbol prop && prop.GetMethod is not null &&
            !IsAccessibilityExportable(prop.GetMethod.DeclaredAccessibility, prop.ContainingAssembly))
        {
            return false;
        }

        return true;
    }

    private bool IsTypeAccessible(ITypeSymbol type)
    {
        switch (type)
        {
            case IArrayTypeSymbol arr:
                return IsTypeAccessible(arr.ElementType);
            case IPointerTypeSymbol ptr:
                return IsTypeAccessible(ptr.PointedAtType);
            case ITypeParameterSymbol:
                return true;
            case INamedTypeSymbol named:
                if (named.SpecialType != SpecialType.None)
                {
                    return true;
                }

                for (var cur = named; cur is not null; cur = cur.ContainingType)
                {
                    if (!IsAccessibilityExportable(cur.DeclaredAccessibility, cur.ContainingAssembly))
                    {
                        return false;
                    }
                }

                foreach (var ta in named.TypeArguments)
                {
                    if (!IsTypeAccessible(ta))
                    {
                        return false;
                    }
                }

                return true;
            default:
                return true;
        }
    }

    private bool IsAccessibilityExportable(Accessibility accessibility, IAssemblySymbol? containingAssembly)
    {
        if (accessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal)
        {
            return true;
        }

        if (_allowInternal &&
            accessibility is Accessibility.Internal or Accessibility.ProtectedAndInternal &&
            SymbolEqualityComparer.Default.Equals(containingAssembly, _sourceAssembly))
        {
            return true;
        }

        return false;
    }

    private static TypeSyntax ParseGlobalType(ITypeSymbol type) =>
        SyntaxFactory.ParseTypeName(TypeRefHelper.ToGlobalTypeName(type));

    private static string EscapeIdentifier(string name) =>
        SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;

    private SyntaxNode Fail(SyntaxNode node)
    {
        _failed = true;
        return node;
    }

    private static bool TryFormatConstant(object? value, ITypeSymbol type, out string literal)
    {
        literal = string.Empty;
        if (value is null)
        {
            literal = type.IsReferenceType || type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
                ? $"default({TypeRefHelper.ToGlobalTypeName(type)})"
                : "null";
            return true;
        }

        if (type.TypeKind == TypeKind.Enum && type is INamedTypeSymbol enumType && enumType.EnumUnderlyingType is not null)
        {
            if (TryFormatConstant(value, enumType.EnumUnderlyingType, out var underlyingLit))
            {
                literal = $"(({TypeRefHelper.ToGlobalTypeName(enumType)})({underlyingLit}))";
                return true;
            }
            return false;
        }

        switch (value)
        {
            case bool b:
                literal = b ? "true" : "false";
                return true;
            case int i:
                literal = i == int.MinValue ? "unchecked((int)-2147483648)" : i.ToString(CultureInfo.InvariantCulture);
                return true;
            case long l:
                literal = l == long.MinValue ? "unchecked((long)-9223372036854775808L)" : l.ToString(CultureInfo.InvariantCulture) + "L";
                return true;
            case uint u:
                literal = u.ToString(CultureInfo.InvariantCulture) + "U";
                return true;
            case ulong ul:
                literal = ul.ToString(CultureInfo.InvariantCulture) + "UL";
                return true;
            case short s:
                literal = $"((short){s.ToString(CultureInfo.InvariantCulture)})";
                return true;
            case ushort us:
                literal = $"((ushort){us.ToString(CultureInfo.InvariantCulture)})";
                return true;
            case byte by:
                literal = $"((byte){by.ToString(CultureInfo.InvariantCulture)})";
                return true;
            case sbyte sb:
                literal = $"((sbyte){sb.ToString(CultureInfo.InvariantCulture)})";
                return true;
            case decimal d:
                literal = d.ToString(CultureInfo.InvariantCulture) + "m";
                return true;
            case double dbl when !double.IsNaN(dbl) && !double.IsInfinity(dbl):
                literal = dbl.ToString("R", CultureInfo.InvariantCulture) + "d";
                return true;
            case float flt when !float.IsNaN(flt) && !float.IsInfinity(flt):
                literal = flt.ToString("R", CultureInfo.InvariantCulture) + "f";
                return true;
            case char ch:
                literal = $"'\\u{((int)ch):X4}'";
                return true;
            case string str:
                literal = TypeRefHelper.EscapeStringLiteral(str);
                return true;
            default:
                return false;
        }
    }
}
