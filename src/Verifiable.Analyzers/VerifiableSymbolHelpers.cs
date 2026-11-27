using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Verifiable.Analyzers;

internal enum VerificationScope
{
    None = 0,
    Candidate = 1,
    Verifiable = 2
}

internal static class VerifiableSymbolHelpers
{
    public const string VerifiableAttr = "Verifiable.Contracts.VerifiableAttribute";
    public const string UnverifiableAttr = "Verifiable.Contracts.UnverifiableAttribute";
    public const string VerifiableCandidateAttr = "Verifiable.Contracts.VerifiableCandidateAttribute";
    public const string InvariantAttr = "Verifiable.Contracts.InvariantAttribute";
    public const string MustConsumeAttr = "Verifiable.Contracts.MustConsumeAttribute";
    public const string ConsumesObligationAttr = "Verifiable.Contracts.ConsumesObligationAttribute";
    public const string InboundPortAttr = "Verifiable.Contracts.InboundPortAttribute";
    public const string GhostAttr = "Verifiable.Contracts.GhostAttribute";
    public const string PureAxiomAttr = "Verifiable.Contracts.PureAxiomAttribute";
    public const string CheckedArithmeticAttr = "Verifiable.Contracts.CheckedArithmeticAttribute";
    public const string SpecTypeName = "Verifiable.Contracts.Spec";

    public static bool HasAttribute(ISymbol symbol, string metadataFullName) =>
        symbol.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == metadataFullName);

    public static bool IsSpecCall(IMethodSymbol method, string specMethodName) =>
        method.ContainingType?.ToDisplayString() == SpecTypeName && method.Name == specMethodName;

    public static bool IsConstrainedStruct(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named)
        {
            return false;
        }

        foreach (var attr in named.GetAttributes())
        {
            if (attr.AttributeClass is { IsGenericType: true } ac)
            {
                var defName = ac.ConstructedFrom.ToDisplayString();
                if (defName is "Verifiable.Contracts.TypeAliasAttribute<TUnderlying>" or "Verifiable.Contracts.RefinedAttribute<TUnderlying>")
                {
                    return true;
                }
            }
        }
        return false;
    }

    public static bool HasInvariantOrRefinedContract(ITypeSymbol type)
    {
        if (IsConstrainedStruct(type))
        {
            return true;
        }

        for (var cur = type as INamedTypeSymbol; cur is not null; cur = cur.BaseType)
        {
            if (HasAttribute(cur, InvariantAttr))
            {
                return true;
            }

            foreach (var member in cur.GetMembers())
            {
                if (member is IMethodSymbol or IPropertySymbol && HasAttribute(member, InvariantAttr))
                {
                    return true;
                }
            }
        }
        return false;
    }

    public static bool IsMustConsumeType(ITypeSymbol? type) =>
        type is INamedTypeSymbol named && named.IsValueType && HasAttribute(named, MustConsumeAttr);

    public static bool ContainsMustConsumeElement(ITypeSymbol? type, out ITypeSymbol? foundType)
    {
        if (type is null)
        {
            foundType = null;
            return false;
        }

        if (IsMustConsumeType(type))
        {
            foundType = type;
            return true;
        }

        if (type is INamedTypeSymbol named && named.IsTupleType)
        {
            foreach (var elem in named.TupleElements)
            {
                if (IsMustConsumeType(elem.Type))
                {
                    foundType = elem.Type;
                    return true;
                }
            }
        }

        foundType = null;
        return false;
    }

    public static VerificationScope GetScope(ISymbol symbol)
    {
        for (var cur = symbol; cur is not null; cur = cur.ContainingSymbol)
        {
            if (cur is INamespaceSymbol or IModuleSymbol)
            {
                break;
            }

            if (HasAttribute(cur, UnverifiableAttr))
            {
                return VerificationScope.None;
            }

            if (HasAttribute(cur, VerifiableAttr))
            {
                return VerificationScope.Verifiable;
            }

            if (HasAttribute(cur, VerifiableCandidateAttr))
            {
                return VerificationScope.Candidate;
            }

            if (cur is IAssemblySymbol)
            {
                return VerificationScope.None;
            }
        }

        if (symbol.ContainingAssembly is { } asm)
        {
            if (HasAttribute(asm, VerifiableAttr))
            {
                return VerificationScope.Verifiable;
            }

            if (HasAttribute(asm, VerifiableCandidateAttr))
            {
                return VerificationScope.Candidate;
            }
        }

        return VerificationScope.None;
    }

    public static bool IsGeneratedByVerifiableGenerators(ISymbol symbol)
    {
        foreach (var loc in symbol.Locations)
        {
            if (loc.IsInSource && loc.SourceTree?.FilePath is { } path)
            {
                if (path.EndsWith(".Refined.g.cs", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".TypeAlias.g.cs", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".Gate.g.cs", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".InboundPort.g.cs", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static bool IsSynthesizedRecordMember(ISymbol symbol)
    {
        if (symbol.IsImplicitlyDeclared)
        {
            return true;
        }

        if (symbol is IMethodSymbol method && method.ContainingType is { IsRecord: true })
        {
            if (method.Name is "<Clone>$" or "PrintMembers" or "Equals" or "GetHashCode" or "Deconstruct" or "op_Equality" or "op_Inequality" or "ToString")
            {
                return true;
            }
        }

        if (symbol is IPropertySymbol prop && prop.ContainingType is { IsRecord: true } && prop.Name == "EqualityContract")
        {
            return true;
        }

        return false;
    }

    public static bool IsMutableCollectionType(ITypeSymbol type, out string displayName)
    {
        if (type is IArrayTypeSymbol arr)
        {
            displayName = arr.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            return true;
        }

        if (type is INamedTypeSymbol named)
        {
            var full = named.OriginalDefinition.ToDisplayString();
            if (full is
                "System.Collections.Generic.List<T>" or
                "System.Collections.Generic.Dictionary<TKey, TValue>" or
                "System.Collections.Generic.HashSet<T>" or
                "System.Collections.Generic.Queue<T>" or
                "System.Collections.Generic.Stack<T>" or
                "System.Collections.Generic.LinkedList<T>" or
                "System.Collections.Generic.SortedDictionary<TKey, TValue>" or
                "System.Collections.Generic.SortedList<TKey, TValue>" or
                "System.Collections.Generic.SortedSet<T>")
            {
                displayName = named.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                return true;
            }
        }

        displayName = string.Empty;
        return false;
    }

    internal static bool IsLinqQueryOperator(IMethodSymbol method) =>
        method.Name is "Any" or "All" or "Count" or "Where" or "Select" or "GroupBy" && IsLinqProvider(method);

    internal static bool IsLinqProvider(IMethodSymbol method)
    {
        if (!method.IsStatic) return false;
        var assembly = method.ContainingAssembly.Identity;
        return (method.ContainingType.ToDisplayString(), assembly.Name) switch
        {
            ("System.Linq.Enumerable", "System.Linq") or
            ("System.Linq.ImmutableArrayExtensions", "System.Collections.Immutable") =>
                IsFrameworkLinqAssembly(assembly),
            ("System.Linq.Enumerable", "System.Core") =>
                assembly.PublicKeyToken.SequenceEqual(new byte[] { 0xb7, 0x7a, 0x5c, 0x56, 0x19, 0x34, 0xe0, 0x89 }),
            _ => false
        };
    }

    internal static bool IsFrameworkLinqAssembly(AssemblyIdentity assembly) =>
        assembly.PublicKeyToken.SequenceEqual(new byte[] { 0xb0, 0x3f, 0x5f, 0x7f, 0x11, 0xd5, 0x0a, 0x3a });

    public static bool IsTrustedPureMethod(IMethodSymbol method)
    {
        if (HasAttribute(method, PureAxiomAttr))
        {
            return true;
        }

        if (HasAttribute(method, UnverifiableAttr))
        {
            return false;
        }

        if (GetScope(method) == VerificationScope.Verifiable)
        {
            return true;
        }

        var containingType = method.ContainingType;
        if (containingType is null)
        {
            return false;
        }

        if (IsConstrainedStruct(containingType))
        {
            return true;
        }

        if (method.MethodKind is MethodKind.BuiltinOperator or MethodKind.UserDefinedOperator or MethodKind.Conversion)
        {
            return true;
        }

        var ns = containingType.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        var typeFull = containingType.OriginalDefinition.ToDisplayString();

        if (Smt.CalendarArithmetic.Supports(method)) return true;

        // Default display names use C# aliases. Match pure helpers by symbol identity;
        // trusting whole primitive types would also admit culture-sensitive and mutating APIs.
        if (method.IsStatic && (containingType.SpecialType switch
        {
            SpecialType.System_String => method.Name is "IsNullOrEmpty" or "IsNullOrWhiteSpace",
            SpecialType.System_Decimal or SpecialType.System_Int32 or SpecialType.System_Int64 or
                SpecialType.System_UInt32 or SpecialType.System_UInt64 => method.Name is "Abs" or "Min" or "Max" or "Clamp" or "Sign",
            SpecialType.System_Char => method.Name is "IsDigit" or "IsLetter" or "IsWhiteSpace",
            _ => false
        }))
        {
            return true;
        }

        if (ns == "System.Collections.Immutable")
        {
            return true;
        }

        if (IsLinqProvider(method))
            return method.ContainingType.Name == "Enumerable" || IsLinqQueryOperator(method);

        if (typeFull is
            "System.Math" or
            "System.MathF" or
            "System.Guid" or
            "System.Enum" or
            "System.ValueTuple" or
            "System.Collections.Generic.Comparer<T>" or
            "System.Collections.Generic.EqualityComparer<T>" or
            "System.Collections.Generic.KeyValuePair<TKey, TValue>" or
            "System.Collections.Generic.KeyValuePair" or
            "System.IComparable<T>" or
            "System.IEquatable<T>")
        {
            if (typeFull == "System.Guid" && method.Name == "NewGuid")
            {
                return false;
            }

            return true;
        }

        if (containingType.IsTupleType || containingType.TypeKind == TypeKind.Enum)
        {
            return true;
        }

        return false;
    }

    public static bool IsNondeterministicMember(ISymbol symbol, out string display)
    {
        var containing = symbol.ContainingType?.OriginalDefinition.ToDisplayString() ?? string.Empty;
        var name = symbol.Name;

        if ((containing is "System.DateTime" or "System.DateTimeOffset") &&
            name is "Now" or "UtcNow" or "Today")
        {
            display = $"{symbol.ContainingType!.Name}.{name}";
            return true;
        }

        if (containing == "System.Guid" && name == "NewGuid")
        {
            display = "Guid.NewGuid()";
            return true;
        }

        if (containing is "System.Random" or "System.Security.Cryptography.RandomNumberGenerator" or "System.Diagnostics.Stopwatch" or "System.Environment" or "System.Console")
        {
            display = $"{symbol.ContainingType!.Name}.{name}";
            return true;
        }

        if (containing.StartsWith("System.IO.", StringComparison.Ordinal) ||
            containing == "System.Net.Http.HttpClient" ||
            containing == "System.Threading.Thread")
        {
            display = $"{symbol.ContainingType!.Name}.{name}";
            return true;
        }

        if (symbol is IFieldSymbol field && field.IsStatic && !field.IsReadOnly && !field.IsConst)
        {
            display = $"mutable static field {field.ContainingType?.Name}.{field.Name}";
            return true;
        }

        display = string.Empty;
        return false;
    }
}
