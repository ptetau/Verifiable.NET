using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Verifiable.Generators;

internal static class TypeRefHelper
{
    public const string ContractsNamespace = "Verifiable.Contracts";
    public const string RefinedAttributeMetadataName = "Verifiable.Contracts.RefinedAttribute`1";
    public const string InvariantAttributeMetadataName = "Verifiable.Contracts.InvariantAttribute";
    public const string RequiresAttributeMetadataName = "Verifiable.Contracts.RequiresAttribute";
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

    public static bool TryGetRefinedUnderlyingType(ITypeSymbol type, out ITypeSymbol? underlyingType, out string? predicate)
    {
        if (type is INamedTypeSymbol named)
        {
            foreach (var attr in named.GetAttributes())
            {
                if (attr.AttributeClass is { IsGenericType: true } attrClass &&
                    attrClass.ConstructedFrom.ToDisplayString() == "Verifiable.Contracts.RefinedAttribute<TUnderlying>" &&
                    attrClass.TypeArguments.Length == 1 &&
                    attr.ConstructorArguments.Length == 1 &&
                    attr.ConstructorArguments[0].Value is string pred)
                {
                    underlyingType = attrClass.TypeArguments[0];
                    predicate = pred;
                    return true;
                }
            }
        }

        underlyingType = null;
        predicate = null;
        return false;
    }

    public static IReadOnlyList<string> GetInvariants(INamedTypeSymbol type)
    {
        var result = new List<string>();
        var stack = new Stack<INamedTypeSymbol>();
        for (var cur = type; cur is not null && cur.SpecialType != SpecialType.System_Object && cur.SpecialType != SpecialType.System_ValueType; cur = cur.BaseType)
        {
            stack.Push(cur);
        }

        while (stack.Count > 0)
        {
            var t = stack.Pop();
            foreach (var attr in t.GetAttributes())
            {
                if (attr.AttributeClass?.ToDisplayString() == InvariantAttributeMetadataName &&
                    attr.ConstructorArguments.Length == 1 &&
                    attr.ConstructorArguments[0].Value is string expr &&
                    !string.IsNullOrWhiteSpace(expr))
                {
                    result.Add(expr);
                }
            }
        }

        return result;
    }

    public static IReadOnlyList<string> GetRequires(IMethodSymbol method)
    {
        var result = new List<string>();
        foreach (var attr in method.GetAttributes())
        {
            if (attr.AttributeClass?.ToDisplayString() == RequiresAttributeMetadataName &&
                attr.ConstructorArguments.Length == 1 &&
                attr.ConstructorArguments[0].Value is string expr &&
                !string.IsNullOrWhiteSpace(expr))
            {
                result.Add(expr);
            }
        }
        return result;
    }

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
}
