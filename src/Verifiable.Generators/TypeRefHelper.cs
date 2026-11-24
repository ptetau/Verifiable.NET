using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Verifiable.Generators;

internal sealed record TypeAliasSpec(
    string Name,
    string Namespace,
    ITypeSymbol UnderlyingType,
    string Predicate,
    bool IsInternal,
    Location DiagnosticLocation,
    IReadOnlyList<string> UsingDirectives)
{
    public string FullName => string.IsNullOrEmpty(Namespace) ? Name : $"{Namespace}.{Name}";
    public string GlobalTypeName => string.IsNullOrEmpty(Namespace) ? $"global::{Name}" : $"global::{Namespace}.{Name}";
}

internal sealed class TypeAliasLookup
{
    public static readonly TypeAliasLookup Empty = new([]);

    private readonly Dictionary<(string Namespace, string Name), TypeAliasSpec> _byFull = new();
    private readonly Dictionary<string, List<TypeAliasSpec>> _byName = new(StringComparer.Ordinal);

    public TypeAliasLookup(IEnumerable<TypeAliasSpec> specs)
    {
        foreach (var spec in specs)
        {
            if (string.IsNullOrWhiteSpace(spec.Name)) continue;
            var key = (spec.Namespace, spec.Name);
            if (!_byFull.ContainsKey(key))
            {
                _byFull[key] = spec;
            }
            if (!_byName.TryGetValue(spec.Name, out var list))
            {
                list = new List<TypeAliasSpec>();
                _byName[spec.Name] = list;
            }
            list.Add(spec);
        }
    }

    public bool TryResolve(
        ITypeSymbol type,
        string contextNamespace,
        IReadOnlyList<string> usings,
        out TypeAliasSpec? spec,
        out bool isNullable)
    {
        isNullable = false;
        if (type is INamedTypeSymbol nullableNamed &&
            nullableNamed.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
            nullableNamed.TypeArguments.Length == 1)
        {
            isNullable = true;
            type = nullableNamed.TypeArguments[0];
        }

        if (type.TypeKind != TypeKind.Error)
        {
            spec = null;
            return false;
        }

        if (type.NullableAnnotation == NullableAnnotation.Annotated)
        {
            isNullable = true;
        }

        var name = type.Name;
        if (string.IsNullOrEmpty(name))
        {
            spec = null;
            return false;
        }

        if (type.ContainingNamespace is { IsGlobalNamespace: false } explicitNs)
        {
            if (_byFull.TryGetValue((explicitNs.ToDisplayString(), name), out spec))
            {
                return true;
            }
        }

        if (!string.IsNullOrEmpty(contextNamespace))
        {
            var cur = contextNamespace;
            while (!string.IsNullOrEmpty(cur))
            {
                if (_byFull.TryGetValue((cur, name), out spec))
                {
                    return true;
                }
                var dot = cur.LastIndexOf('.');
                cur = dot > 0 ? cur.Substring(0, dot) : string.Empty;
            }
        }

        foreach (var u in usings)
        {
            if (_byFull.TryGetValue((u, name), out spec))
            {
                return true;
            }
        }

        if (_byFull.TryGetValue((string.Empty, name), out spec))
        {
            return true;
        }

        if (_byName.TryGetValue(name, out var candidates) && candidates.Count == 1)
        {
            spec = candidates[0];
            return true;
        }

        spec = null;
        return false;
    }
}

internal static class TypeRefHelper
{
    public const string ContractsNamespace = "Verifiable.Contracts";
    public const string RefinedAttributeMetadataName = "Verifiable.Contracts.RefinedAttribute`1";
    public const string TypeAliasAttributeMetadataName = "Verifiable.Contracts.TypeAliasAttribute`1";
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

    public static List<TypeAliasSpec> ExtractTypeAliases(
        IEnumerable<AttributeData> attributes,
        string defaultNamespace,
        Location fallbackLocation,
        IReadOnlyList<string>? fallbackUsings = null)
    {
        var result = new List<TypeAliasSpec>();
        foreach (var attr in attributes)
        {
            if (attr.AttributeClass is not { IsGenericType: true } ac ||
                ac.ConstructedFrom.ToDisplayString() != "Verifiable.Contracts.TypeAliasAttribute<TUnderlying>" ||
                ac.TypeArguments.Length != 1)
            {
                continue;
            }

            if (attr.ConstructorArguments.Length < 2)
            {
                continue;
            }

            var name = attr.ConstructorArguments[0].Value as string ?? string.Empty;
            var predicate = attr.ConstructorArguments[1].Value as string ?? string.Empty;
            var targetNs = defaultNamespace;
            var isInternal = false;

            foreach (var namedArg in attr.NamedArguments)
            {
                if (namedArg.Key == "Namespace" && namedArg.Value.Value is string nsVal)
                {
                    targetNs = nsVal.Trim();
                }
                else if (namedArg.Key == "IsInternal" && namedArg.Value.Value is bool bVal)
                {
                    isInternal = bVal;
                }
            }

            var loc = attr.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? fallbackLocation;
            var usings = attr.ApplicationSyntaxReference?.SyntaxTree is { } st
                ? CollectUsings(st)
                : (fallbackUsings ?? Array.Empty<string>());

            result.Add(new TypeAliasSpec(
                name,
                targetNs,
                ac.TypeArguments[0],
                predicate,
                isInternal,
                loc,
                usings));
        }

        return result;
    }

    public static string FormatGlobalTypeName(
        ITypeSymbol type,
        string contextNamespace,
        IReadOnlyList<string> usings,
        TypeAliasLookup aliases)
    {
        if (aliases.TryResolve(type, contextNamespace, usings, out var spec, out var isNullable))
        {
            return isNullable ? spec!.GlobalTypeName + "?" : spec!.GlobalTypeName;
        }

        if (type is INamedTypeSymbol named)
        {
            if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
                named.TypeArguments.Length == 1)
            {
                return FormatGlobalTypeName(named.TypeArguments[0], contextNamespace, usings, aliases) + "?";
            }

            if (named.IsGenericType && !named.IsUnboundGenericType)
            {
                var orig = ToGlobalTypeName(named.OriginalDefinition);
                var ltIdx = orig.IndexOf('<');
                if (ltIdx > 0)
                {
                    var formattedArgs = named.TypeArguments.Select(a => FormatGlobalTypeName(a, contextNamespace, usings, aliases));
                    var suffix = named.NullableAnnotation == NullableAnnotation.Annotated && named.IsReferenceType ? "?" : string.Empty;
                    return orig.Substring(0, ltIdx) + "<" + string.Join(", ", formattedArgs) + ">" + suffix;
                }
            }
        }

        return ToGlobalTypeName(type);
    }
}
