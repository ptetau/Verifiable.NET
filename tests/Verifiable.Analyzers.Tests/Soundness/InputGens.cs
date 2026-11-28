using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using GalaxyCheck;
using Microsoft.CodeAnalysis;

namespace Verifiable.Analyzers.Tests.Soundness;

/// <summary>
/// GalaxyCheck generators of inputs for the runtime cross-check. Values lean towards the places bugs hide: small
/// numbers, zero, the edges of each type, empty and short strings, and odd characters.
/// </summary>
internal static class InputGens
{
    private static readonly string[] Words =
        ["", "a", "b", "ab", "ba", "abc", "aab", "A", " ", "  a ", "\0", "é", "😀", "aaaaaaaaaaaaaaaa", "Hello, World"];

    private static readonly char[] Chars =
        ['a', 'b', 'z', 'A', 'Z', '0', '9', ' ', '\0', '\n', '\u007F', '\u0080', 'é', '\uD800', '￿', (char)65, (char)255, (char)256];

    public static IGen<object?>? For(ITypeSymbol type)
    {
        var isNullable = type.NullableAnnotation == NullableAnnotation.Annotated;
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullableValue)
        {
            var inner = For(nullableValue.TypeArguments[0]);
            return inner is null ? null : Gen.Choose((inner, 8), (Gen.Constant<object?>(null), 2));
        }

        IGen<object?>? gen = type.SpecialType switch
        {
            SpecialType.System_Boolean => Gen.Boolean().Select(b => (object?)b),
            SpecialType.System_SByte => Integers(sbyte.MinValue, sbyte.MaxValue).Select(v => (object?)(sbyte)v),
            SpecialType.System_Byte => Integers(byte.MinValue, byte.MaxValue).Select(v => (object?)(byte)v),
            SpecialType.System_Int16 => Integers(short.MinValue, short.MaxValue).Select(v => (object?)(short)v),
            SpecialType.System_UInt16 => Integers(ushort.MinValue, ushort.MaxValue).Select(v => (object?)(ushort)v),
            SpecialType.System_Int32 => Integers(int.MinValue, int.MaxValue).Select(v => (object?)(int)v),
            SpecialType.System_UInt32 => Integers(uint.MinValue, uint.MaxValue).Select(v => (object?)(uint)v),
            SpecialType.System_Int64 => Integers(long.MinValue, long.MaxValue).Select(v => (object?)v),
            SpecialType.System_UInt64 => Gen.Choose(
                (Gen.UInt64().Between(0, 20), 6),
                (Gen.Element(new[] { ulong.MaxValue, ulong.MaxValue - 1, (ulong)long.MaxValue, (ulong)long.MaxValue + 1, (ulong)uint.MaxValue + 1 }), 2),
                (Gen.UInt64(), 2)).Select(v => (object?)v),
            SpecialType.System_Char => Gen.Choose(
                (Gen.Element(Chars), 6),
                (Gen.Int32().Between(0, char.MaxValue).Select(i => (char)i), 4)).Select(c => (object?)c),
            SpecialType.System_String => Gen.Choose(
                (Gen.Element(Words), 7),
                (Gen.String(), 3)).Select(s => (object?)s),
            SpecialType.System_Decimal => Gen.Choose(
                (Gen.Int32().Between(-20, 20).Select(i => (decimal)i), 5),
                (Gen.Element(new[] { 0.5m, -0.5m, 0.1m, 1.0000000000000000000000000001m, decimal.MaxValue, decimal.MinValue, decimal.MaxValue - 1, 79228162514264337593543950334.5m }), 3),
                (Gen.Decimal(), 2)).Select(d => (object?)d),
            _ => null,
        };

        if (gen is null) return null;
        return isNullable && type.IsReferenceType ? Gen.Choose((gen, 8), (Gen.Constant<object?>(null), 2)) : gen;
    }

    /// <summary>Mostly small values, often a value at or next to an edge of the type, sometimes anything at all.</summary>
    private static IGen<long> Integers(long min, long max)
    {
        var edges = new[] { min, min + 1, -2L, -1L, 0L, 1L, 2L, max - 1, max, 255L, 256L, 65535L, 65536L, int.MaxValue, (long)int.MaxValue + 1, int.MinValue, (long)int.MinValue - 1 }
            .Where(v => v >= min && v <= max)
            .Distinct()
            .ToArray();
        return Gen.Choose(
            (Gen.Int64().Between(Math.Max(min, -20), Math.Min(max, 20)), 6),
            (Gen.Element(edges), 3),
            (Gen.Int64().Between(min, max), 1));
    }

    public static IGen<object?[]> Combine(IReadOnlyList<IGen<object?>> gens)
    {
        IGen<ImmutableList<object?>> acc = Gen.Constant(ImmutableList<object?>.Empty);
        foreach (var g in gens)
        {
            var current = acc;
            acc = from list in current
                  from v in g
                  select list.Add(v);
        }

        return acc.Select(l => l.ToArray());
    }
}
