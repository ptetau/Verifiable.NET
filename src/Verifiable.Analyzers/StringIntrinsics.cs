using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Verifiable.Analyzers;

internal enum StringIntrinsicKind
{
    None = 0,
    IsNullOrEmpty,
    IsNullOrWhiteSpace,
    Trim,
    ContainsString,
    ContainsChar,
    StartsWithString,
    StartsWithChar,
    EndsWithString,
    EndsWithChar,
    JoinStringParams,
    JoinCharParams,
    CharToString
}

internal static class StringIntrinsics
{
    public const int MaxBoundedJoinElements = 32;

    // System.StringComparison.Ordinal == 4
    private const int StringComparisonOrdinal = 4;

    public static bool IsTrustedStringMethod(IMethodSymbol method)
    {
        if (IsCharToString(method))
        {
            return true;
        }

        if (method.ContainingType?.SpecialType != SpecialType.System_String)
        {
            return false;
        }

        if (method.IsStatic)
        {
            return method.Name switch
            {
                "IsNullOrEmpty" when method.Parameters.Length == 1 &&
                    method.Parameters[0].Type.SpecialType == SpecialType.System_String => true,
                "IsNullOrWhiteSpace" when method.Parameters.Length == 1 &&
                    method.Parameters[0].Type.SpecialType == SpecialType.System_String => true,
                "Join" => IsSupportedJoinOverload(method),
                _ => false
            };
        }

        return method.Name switch
        {
            "Trim" when method.Parameters.Length == 0 => true,
            "Contains" => IsSupportedSearchOverload(method, allowCharWithComparison: true),
            "StartsWith" => IsSupportedSearchOverload(method, allowCharWithComparison: false),
            "EndsWith" => IsSupportedSearchOverload(method, allowCharWithComparison: false),
            _ => false
        };
    }

    public static bool IsTrustedStringInvocation(IInvocationOperation inv)
    {
        var method = inv.TargetMethod;
        if (!IsTrustedStringMethod(method))
        {
            return false;
        }

        // If a StringComparison parameter is present, only compile-time Ordinal is culture-independent and supported.
        foreach (var arg in inv.Arguments)
        {
            if (arg.Parameter?.Type.ToDisplayString() == "System.StringComparison")
            {
                if (!IsOrdinalComparison(arg.Value))
                {
                    return false;
                }
            }
        }

        return true;
    }

    public static StringIntrinsicKind Classify(IInvocationOperation inv, out string? error)
    {
        error = null;
        var method = inv.TargetMethod;
        if (IsCharToString(method))
        {
            return StringIntrinsicKind.CharToString;
        }

        if (method.ContainingType?.SpecialType != SpecialType.System_String)
        {
            return StringIntrinsicKind.None;
        }

        if (method.IsStatic)
        {
            if (method.Name == "IsNullOrEmpty" && method.Parameters.Length == 1 &&
                method.Parameters[0].Type.SpecialType == SpecialType.System_String)
            {
                return StringIntrinsicKind.IsNullOrEmpty;
            }

            if (method.Name == "IsNullOrWhiteSpace" && method.Parameters.Length == 1 &&
                method.Parameters[0].Type.SpecialType == SpecialType.System_String)
            {
                return StringIntrinsicKind.IsNullOrWhiteSpace;
            }

            if (method.Name == "Join" && IsSupportedJoinOverload(method))
            {
                return method.Parameters[0].Type.SpecialType == SpecialType.System_Char
                    ? StringIntrinsicKind.JoinCharParams
                    : StringIntrinsicKind.JoinStringParams;
            }

            return StringIntrinsicKind.None;
        }

        if (method.Name == "Trim" && method.Parameters.Length == 0)
        {
            return StringIntrinsicKind.Trim;
        }

        if (method.Name is "Contains" or "StartsWith" or "EndsWith")
        {
            var allowCharWithComp = method.Name == "Contains";
            if (!IsSupportedSearchOverload(method, allowCharWithComp))
            {
                return StringIntrinsicKind.None;
            }

            if (method.Parameters.Length == 2)
            {
                var compArg = FindArgumentForParameter(inv, 1);
                if (compArg is null || !IsOrdinalComparison(compArg.Value))
                {
                    error = $"Only StringComparison.Ordinal is supported for 'string.{method.Name}'.";
                    return StringIntrinsicKind.None;
                }
            }

            var isChar = method.Parameters[0].Type.SpecialType == SpecialType.System_Char;
            return method.Name switch
            {
                "Contains" => isChar ? StringIntrinsicKind.ContainsChar : StringIntrinsicKind.ContainsString,
                "StartsWith" => isChar ? StringIntrinsicKind.StartsWithChar : StringIntrinsicKind.StartsWithString,
                "EndsWith" => isChar ? StringIntrinsicKind.EndsWithChar : StringIntrinsicKind.EndsWithString,
                _ => StringIntrinsicKind.None
            };
        }

        return StringIntrinsicKind.None;
    }

    /// <summary>
    /// <c>c.ToString()</c> on a <c>char</c>: the one-character string holding that UTF-16 code unit. It does not
    /// depend on culture.
    /// </summary>
    public static bool IsCharToString(IMethodSymbol method) =>
        method.ContainingType?.SpecialType == SpecialType.System_Char &&
        !method.IsStatic &&
        method.Name == "ToString" &&
        method.Parameters.Length == 0;

    public static bool IsSupportedJoinOverload(IMethodSymbol method)
    {
        if (method.Parameters.Length != 2)
        {
            return false;
        }

        var sepType = method.Parameters[0].Type;
        if (sepType.SpecialType is not (SpecialType.System_String or SpecialType.System_Char))
        {
            return false;
        }

        var valuesType = method.Parameters[1].Type;
        if (valuesType is IArrayTypeSymbol arr && arr.ElementType.SpecialType == SpecialType.System_String)
        {
            return true;
        }

        if (valuesType is INamedTypeSymbol named && named.IsGenericType && named.TypeArguments.Length == 1 &&
            named.TypeArguments[0].SpecialType == SpecialType.System_String)
        {
            var orig = named.OriginalDefinition.ToDisplayString();
            return orig is
                "System.ReadOnlySpan<T>" or
                "System.Collections.Generic.IEnumerable<T>";
        }

        return false;
    }

    private static bool IsSupportedSearchOverload(IMethodSymbol method, bool allowCharWithComparison)
    {
        if (method.Parameters.Length == 1)
        {
            return method.Parameters[0].Type.SpecialType is SpecialType.System_String or SpecialType.System_Char;
        }

        if (method.Parameters.Length == 2 &&
            method.Parameters[1].Type.ToDisplayString() == "System.StringComparison")
        {
            if (method.Parameters[0].Type.SpecialType == SpecialType.System_String)
            {
                return true;
            }

            if (allowCharWithComparison && method.Parameters[0].Type.SpecialType == SpecialType.System_Char)
            {
                return true;
            }
        }

        return false;
    }

    private static IArgumentOperation? FindArgumentForParameter(IInvocationOperation inv, int ordinal)
    {
        foreach (var arg in inv.Arguments)
        {
            if (arg.Parameter?.Ordinal == ordinal)
            {
                return arg;
            }
        }

        return ordinal < inv.Arguments.Length ? inv.Arguments[ordinal] : null;
    }

    private static bool IsOrdinalComparison(IOperation op)
    {
        var cur = op;
        while (cur is IConversionOperation conv)
        {
            cur = conv.Operand;
        }

        if (cur.ConstantValue.HasValue && cur.ConstantValue.Value is not null)
        {
            return Convert.ToInt32(cur.ConstantValue.Value) == StringComparisonOrdinal;
        }

        return false;
    }
}
