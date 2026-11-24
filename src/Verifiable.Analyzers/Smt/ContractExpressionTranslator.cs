using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Microsoft.CodeAnalysis;

namespace Verifiable.Analyzers.Smt;

internal sealed class ContractLoweringException(string message) : Exception(message);

internal sealed record SmtTypedTerm(string Term, ITypeSymbol Type);

internal sealed class ContractExpressionTranslator
{
    private readonly Compilation _compilation;
    private readonly SmtTypeSystem _types;

    public ContractExpressionTranslator(Compilation compilation, SmtTypeSystem types)
    {
        _compilation = compilation;
        _types = types;
    }

    public SmtTypedTerm? TryTranslateMemberOnReceiver(SmtTypedTerm receiver, string memberName)
    {
        if (receiver.Type.SpecialType == SpecialType.System_String && memberName == "Length")
        {
            return new SmtTypedTerm(
                $"(ite (= {receiver.Term} str-null) 0 (str.len (str-text {receiver.Term})))",
                _compilation.GetSpecialType(SpecialType.System_Int32));
        }

        if (memberName == "Length" &&
            SmtTypeSystem.TryGetConstrainedUnderlying(receiver.Type, out var strUnder) &&
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

        return null;
    }

    public SmtTypedTerm UnwrapRefinedIfApplicable(SmtTypedTerm term)
    {
        if (SmtTypeSystem.TryGetConstrainedUnderlying(term.Type, out var underlying) &&
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

        if (SmtTypeSystem.TryGetConstrainedUnderlying(value.Type, out _))
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
}
