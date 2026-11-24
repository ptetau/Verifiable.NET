using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Verifiable.Generators;

internal static class TypeRefHelper
{
    public const string ContractsNamespace = "Verifiable.Contracts";
    public const string TypeAliasAttributeMetadataName = "Verifiable.Contracts.TypeAliasAttribute`1";
    public const string RefinedAttributeMetadataName = "Verifiable.Contracts.RefinedAttribute`1";
    public const string InvariantAttributeMetadataName = "Verifiable.Contracts.InvariantAttribute";
    public const string InboundPortAttributeMetadataName = "Verifiable.Contracts.InboundPortAttribute";
    public const string GhostAttributeMetadataName = "Verifiable.Contracts.GhostAttribute";

    public static string ToGlobalTypeName(ITypeSymbol type) =>
        type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    public static string EscapeStringLiteral(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        sb.Append('"');
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\r': sb.Append("\\r"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(ch); break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    public static string GetNamespace(ISymbol symbol)
    {
        var ns = symbol.ContainingNamespace;
        return ns is null || ns.IsGlobalNamespace ? string.Empty : ns.ToDisplayString();
    }

    public static string SanitizeHintName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            sb.Append(char.IsLetterOrDigit(ch) || ch == '.' || ch == '_' || ch == '-' ? ch : '_');
        }
        return sb.ToString();
    }

    public static bool TryGetConstrainedUnderlyingType(ITypeSymbol type, out ITypeSymbol? underlyingType)
    {
        if (type is INamedTypeSymbol named)
        {
            foreach (var attr in named.GetAttributes())
            {
                if (attr.AttributeClass is { IsGenericType: true, TypeArguments.Length: 1 } attrClass)
                {
                    var defName = attrClass.ConstructedFrom.ToDisplayString();
                    if (defName is "Verifiable.Contracts.TypeAliasAttribute<TUnderlying>" or "Verifiable.Contracts.RefinedAttribute<TUnderlying>")
                    {
                        underlyingType = attrClass.TypeArguments[0];
                        return true;
                    }
                }
            }
        }

        underlyingType = null;
        return false;
    }

    public static bool TryFindIsValidMethod(
        INamedTypeSymbol type,
        ITypeSymbol underlyingType,
        out IMethodSymbol? method,
        out ExpressionSyntax? bodyExpression,
        out string? parameterName,
        out bool acceptsNullableParam)
    {
        foreach (var m in type.GetMembers("IsValid").OfType<IMethodSymbol>())
        {
            if (m.IsStatic &&
                m.DeclaredAccessibility == Accessibility.Public &&
                m.ReturnType.SpecialType == SpecialType.System_Boolean &&
                m.Parameters.Length == 1 &&
                SymbolEqualityComparer.Default.Equals(m.Parameters[0].Type, underlyingType))
            {
                method = m;
                parameterName = m.Parameters[0].Name;
                acceptsNullableParam = m.Parameters[0].NullableAnnotation == NullableAnnotation.Annotated;
                bodyExpression = TryExtractMethodBodyExpression(m);
                return bodyExpression is not null;
            }
        }

        method = null;
        bodyExpression = null;
        parameterName = null;
        acceptsNullableParam = false;
        return false;
    }

    private static ExpressionSyntax? TryExtractMethodBodyExpression(IMethodSymbol method)
    {
        foreach (var synRef in method.DeclaringSyntaxReferences)
        {
            if (synRef.GetSyntax() is MethodDeclarationSyntax md)
            {
                if (md.ExpressionBody is not null)
                {
                    return md.ExpressionBody.Expression;
                }

                if (md.Body is { Statements.Count: 1 } body &&
                    body.Statements[0] is ReturnStatementSyntax ret &&
                    ret.Expression is not null)
                {
                    return ret.Expression;
                }
            }
        }

        return null;
    }

    public static IReadOnlyList<(string Name, bool IsMethod)> GetInvariantMembers(INamedTypeSymbol type)
    {
        var result = new List<(string Name, bool IsMethod)>();
        var stack = new Stack<INamedTypeSymbol>();
        for (var cur = type; cur is not null && cur.SpecialType != SpecialType.System_Object && cur.SpecialType != SpecialType.System_ValueType; cur = cur.BaseType)
        {
            stack.Push(cur);
        }

        while (stack.Count > 0)
        {
            var t = stack.Pop();
            foreach (var m in t.GetMembers().OfType<IMethodSymbol>())
            {
                if (!m.IsStatic &&
                    m.MethodKind == MethodKind.Ordinary &&
                    m.Parameters.Length == 0 &&
                    m.ReturnType.SpecialType == SpecialType.System_Boolean &&
                    HasInvariantAttribute(m))
                {
                    result.Add((m.Name, true));
                }
            }

            foreach (var p in t.GetMembers().OfType<IPropertySymbol>())
            {
                if (!p.IsStatic &&
                    !p.IsIndexer &&
                    p.Type.SpecialType == SpecialType.System_Boolean &&
                    HasInvariantAttribute(p))
                {
                    result.Add((p.Name, false));
                }
            }
        }

        return result;
    }

    public static IReadOnlyList<ExpressionSyntax> GetLeadingSpecRequiresExpressions(IMethodSymbol method)
    {
        var result = new List<ExpressionSyntax>();
        foreach (var synRef in method.DeclaringSyntaxReferences)
        {
            if (synRef.GetSyntax() is MethodDeclarationSyntax md && md.Body is { } body)
            {
                foreach (var stmt in body.Statements)
                {
                    if (stmt is not ExpressionStatementSyntax exprStmt ||
                        exprStmt.Expression is not InvocationExpressionSyntax inv ||
                        !TryMatchSpecMethodName(inv.Expression, out var specMethodName))
                    {
                        break;
                    }

                    if (specMethodName == "Requires" && inv.ArgumentList.Arguments.Count == 1)
                    {
                        result.Add(inv.ArgumentList.Arguments[0].Expression);
                    }
                }
            }
        }

        return result;
    }

    private static bool TryMatchSpecMethodName(ExpressionSyntax expr, out string methodName)
    {
        methodName = string.Empty;
        if (expr is MemberAccessExpressionSyntax ma)
        {
            var rawName = ma.Name.Identifier.ValueText;
            var isSpecReceiver = ma.Expression is IdentifierNameSyntax { Identifier.ValueText: "Spec" }
                || (ma.Expression is MemberAccessExpressionSyntax recMa && recMa.Name.Identifier.ValueText == "Spec");
            if (isSpecReceiver)
            {
                methodName = rawName;
                return true;
            }
        }
        return false;
    }

    public static bool HasInvariantAttribute(ISymbol symbol) =>
        symbol.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == InvariantAttributeMetadataName);

    public static bool HasGhostAttribute(ISymbol symbol) =>
        symbol.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == GhostAttributeMetadataName);

    public static bool IsImmutableCollection(
        ITypeSymbol type,
        out string kind,
        out ITypeSymbol? elemOrKeyType,
        out ITypeSymbol? valueType)
    {
        if (type is INamedTypeSymbol named && named.IsGenericType)
        {
            var orig = named.OriginalDefinition.ToDisplayString();
            switch (orig)
            {
                case "System.Collections.Immutable.ImmutableList<T>":
                case "System.Collections.Immutable.ImmutableArray<T>":
                case "System.Collections.Immutable.ImmutableHashSet<T>":
                    kind = named.Name;
                    elemOrKeyType = named.TypeArguments[0];
                    valueType = null;
                    return true;
                case "System.Collections.Immutable.ImmutableDictionary<TKey, TValue>":
                    kind = named.Name;
                    elemOrKeyType = named.TypeArguments[0];
                    valueType = named.TypeArguments[1];
                    return true;
            }
        }

        kind = string.Empty;
        elemOrKeyType = null;
        valueType = null;
        return false;
    }

    public static bool IsScalarOrKnownBclType(ITypeSymbol type)
    {
        if (type.SpecialType != SpecialType.None)
        {
            return true;
        }

        if (type.TypeKind == TypeKind.Enum)
        {
            return true;
        }

        var full = type.ToDisplayString();
        return full is "System.Guid"
            or "System.DateTime"
            or "System.DateTimeOffset"
            or "System.DateOnly"
            or "System.TimeOnly"
            or "System.TimeSpan";
    }

    public static IReadOnlyList<string> CollectUsings(SyntaxTree? tree)
    {
        var list = new List<string>();
        if (tree?.GetRoot() is CompilationUnitSyntax cu)
        {
            foreach (var u in cu.Usings)
            {
                if (u.Alias is null && u.StaticKeyword.IsKind(SyntaxKind.None) && u.Name is not null)
                {
                    var ns = u.Name.ToString();
                    if (!string.IsNullOrWhiteSpace(ns))
                    {
                        list.Add(ns);
                    }
                }
            }
        }
        return list.Distinct(StringComparer.Ordinal).ToList();
    }
}
