using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
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
            var lenInt = _compilation.GetSpecialType(SpecialType.System_Int32);
            return new SmtTypedTerm(
                IntToTargetInt($"(ite (= {receiver.Term} str-null) 0 (str.len (str-text {receiver.Term})))", lenInt),
                lenInt);
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

        if (_types.TryGetSequenceInfo(receiver.Type, out _, out var seqId, out _, out var isImmArr))
        {
            if (memberName is "Count" or "Length")
            {
                return new SmtTypedTerm($"(seq_count_{seqId} {receiver.Term})", _compilation.GetSpecialType(SpecialType.System_Int32));
            }
            if (isImmArr && memberName == "IsDefault")
            {
                return new SmtTypedTerm($"(seq_is_default_{seqId} {receiver.Term})", _compilation.GetSpecialType(SpecialType.System_Boolean));
            }
            if (isImmArr && memberName == "IsDefaultOrEmpty")
            {
                return new SmtTypedTerm($"(or (seq_is_default_{seqId} {receiver.Term}) (= (seq_count_{seqId} {receiver.Term}) 0))", _compilation.GetSpecialType(SpecialType.System_Boolean));
            }
            if (memberName == "IsEmpty")
            {
                return new SmtTypedTerm($"(= (seq_count_{seqId} {receiver.Term}) 0)", _compilation.GetSpecialType(SpecialType.System_Boolean));
            }
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

        var calKind = CalendarArithmetic.Classify(receiver.Type);
        if (calKind != CalendarTypeKind.None)
        {
            var calTerm = TryTranslateCalendarMember(receiver, calKind, memberName);
            if (calTerm is not null)
            {
                return calTerm;
            }
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

    private SmtTypedTerm? TryTranslateCalendarMember(SmtTypedTerm receiver, CalendarTypeKind kind, string memberName)
    {
        var intType = _compilation.GetSpecialType(SpecialType.System_Int32);
        var longType = _compilation.GetSpecialType(SpecialType.System_Int64);
        var doubleType = _compilation.GetSpecialType(SpecialType.System_Double);
        var dayOfWeekType = _compilation.GetTypeByMetadataName("System.DayOfWeek") ?? (ITypeSymbol)intType;
        var timeSpanType = _compilation.GetTypeByMetadataName("System.TimeSpan") ?? (ITypeSymbol)longType;

        switch (kind)
        {
            case CalendarTypeKind.DateOnly:
                if (memberName == "DayNumber")
                    return new SmtTypedTerm(IntToTargetInt(receiver.Term, intType), intType);
                if (memberName == "DayOfWeek")
                    return new SmtTypedTerm($"(mod (+ {receiver.Term} 1) 7)", dayOfWeekType);
                break;

            case CalendarTypeKind.DateTime:
                if (memberName == "Ticks")
                    return new SmtTypedTerm(IntToTargetInt(receiver.Term, longType), longType);
                if (memberName == "Date")
                    return new SmtTypedTerm($"(* (div {receiver.Term} {CalendarArithmetic.TicksPerDay}) {CalendarArithmetic.TicksPerDay})", receiver.Type);
                if (memberName == "TimeOfDay")
                    return new SmtTypedTerm($"(mod {receiver.Term} {CalendarArithmetic.TicksPerDay})", timeSpanType);
                if (memberName == "DayOfWeek")
                    return new SmtTypedTerm($"(mod (+ (div {receiver.Term} {CalendarArithmetic.TicksPerDay}) 1) 7)", dayOfWeekType);
                break;

            case CalendarTypeKind.DateTimeOffset:
                if (memberName == "UtcTicks")
                    return new SmtTypedTerm(IntToTargetInt($"(dto_utc_ticks {receiver.Term})", longType), longType);
                if (memberName == "Offset")
                    return new SmtTypedTerm($"(dto_offset_ticks {receiver.Term})", timeSpanType);
                if (memberName == "Ticks")
                    return new SmtTypedTerm(IntToTargetInt($"(+ (dto_utc_ticks {receiver.Term}) (dto_offset_ticks {receiver.Term}))", longType), longType);
                if (memberName == "DayOfWeek")
                    return new SmtTypedTerm($"(mod (+ (div (+ (dto_utc_ticks {receiver.Term}) (dto_offset_ticks {receiver.Term})) {CalendarArithmetic.TicksPerDay}) 1) 7)", dayOfWeekType);
                break;

            case CalendarTypeKind.TimeSpan:
                if (memberName == "Ticks")
                    return new SmtTypedTerm(IntToTargetInt(receiver.Term, longType), longType);
                if (memberName == "Days")
                    return new SmtTypedTerm(IntToTargetInt(SmtTruncDiv(receiver.Term, CalendarArithmetic.TicksPerDay), intType), intType);
                if (memberName == "Hours")
                    return new SmtTypedTerm(IntToTargetInt(SmtTruncRem(SmtTruncDiv(receiver.Term, CalendarArithmetic.TicksPerHour), 24), intType), intType);
                if (memberName == "Minutes")
                    return new SmtTypedTerm(IntToTargetInt(SmtTruncRem(SmtTruncDiv(receiver.Term, CalendarArithmetic.TicksPerMinute), 60), intType), intType);
                if (memberName == "Seconds")
                    return new SmtTypedTerm(IntToTargetInt(SmtTruncRem(SmtTruncDiv(receiver.Term, CalendarArithmetic.TicksPerSecond), 60), intType), intType);
                if (memberName == "Milliseconds")
                    return new SmtTypedTerm(IntToTargetInt(SmtTruncRem(SmtTruncDiv(receiver.Term, CalendarArithmetic.TicksPerMillisecond), 1000), intType), intType);
                if (memberName == "TotalDays")
                    return new SmtTypedTerm($"(/ (to_real {receiver.Term}) {CalendarArithmetic.TicksPerDay}.0)", doubleType);
                if (memberName == "TotalHours")
                    return new SmtTypedTerm($"(/ (to_real {receiver.Term}) {CalendarArithmetic.TicksPerHour}.0)", doubleType);
                if (memberName == "TotalMinutes")
                    return new SmtTypedTerm($"(/ (to_real {receiver.Term}) {CalendarArithmetic.TicksPerMinute}.0)", doubleType);
                if (memberName == "TotalSeconds")
                    return new SmtTypedTerm($"(/ (to_real {receiver.Term}) {CalendarArithmetic.TicksPerSecond}.0)", doubleType);
                if (memberName == "TotalMilliseconds")
                    return new SmtTypedTerm($"(/ (to_real {receiver.Term}) {CalendarArithmetic.TicksPerMillisecond}.0)", doubleType);
                break;
        }

        return null;
    }

    private string IntToTargetInt(string intTerm, ITypeSymbol targetType)
    {
        var sort = _types.GetSort(targetType);
        if (sort == "(_ BitVec 32)") return $"((_ int2bv 32) {intTerm})";
        if (sort == "(_ BitVec 64)") return $"((_ int2bv 64) {intTerm})";
        return intTerm;
    }

    private static string SmtTruncDiv(string x, long divisor) =>
        $"(ite (< {x} 0) (- (div (- {x}) {divisor})) (div {x} {divisor}))";

    private static string SmtTruncRem(string x, long divisor) =>
        $"(- {x} (* {divisor} {SmtTruncDiv(x, divisor)}))";

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

        // A string seen through an interface or object (IEquatable<string>, object) is still the same VerStr
        // value, so keep its type: the null state and the string operations must not be lost.
        if (value.Type.SpecialType == SpecialType.System_String &&
            (targetType.TypeKind == TypeKind.Interface || targetType.SpecialType == SpecialType.System_Object))
        {
            return value;
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

        if (dstSort == "(_ BitVec 32)")
        {
            if (srcSort == "Int")
                return new SmtTypedTerm($"((_ int2bv 32) {value.Term})", targetType);
            if (srcSort == "(_ BitVec 64)")
                return new SmtTypedTerm($"((_ extract 31 0) {value.Term})", targetType);
        }

        if (dstSort == "(_ BitVec 64)")
        {
            if (srcSort == "(_ BitVec 32)")
                return new SmtTypedTerm($"((_ sign_extend 32) {value.Term})", targetType);
            if (srcSort == "Int")
                return new SmtTypedTerm($"((_ int2bv 64) {value.Term})", targetType);
        }

        // Tuples that differ only in element names or element types (`(a, b)` returned as `(int X, int Y)`)
        // get different sorts, so convert element by element, as C# does.
        if (value.Type is INamedTypeSymbol { IsTupleType: true } &&
            targetType is INamedTypeSymbol { IsTupleType: true } &&
            _types.TryGetComposite(value.Type, out var srcTuple) &&
            _types.TryGetComposite(targetType, out var dstTuple) &&
            srcTuple!.Fields.Count == dstTuple!.Fields.Count)
        {
            if (dstTuple.Fields.Count == 0)
            {
                return new SmtTypedTerm(dstTuple.ConstructorName, targetType);
            }

            var elems = new System.Collections.Generic.List<string>(dstTuple.Fields.Count);
            for (var i = 0; i < dstTuple.Fields.Count; i++)
            {
                var src = srcTuple.Fields[i];
                elems.Add(ConvertTo(new SmtTypedTerm($"({src.AccessorName} {value.Term})", src.Type), dstTuple.Fields[i].Type).Term);
            }

            return new SmtTypedTerm($"({dstTuple.ConstructorName} {string.Join(" ", elems)})", targetType);
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
            var decType = left.Type.SpecialType == SpecialType.System_Decimal || right.Type.SpecialType == SpecialType.System_Decimal
                ? _compilation.GetSpecialType(SpecialType.System_Decimal)
                : _compilation.GetSpecialType(SpecialType.System_Double);
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

    public static string FormatDoubleLiteral(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new ContractLoweringException("Non-finite floating-point literals (NaN/Infinity) are not supported in SMT.");
        }
        if (value == 0.0) return "0.0";

        // Format via exact round-trip decimal string -> exact SMT rational
        var s = value.ToString("R", CultureInfo.InvariantCulture);
        if (decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var dec))
        {
            return FormatDecimalLiteral(dec);
        }

        // Fallback for very small/large doubles via binary exponent exact representation
        var bits = BitConverter.DoubleToInt64Bits(value);
        var isNeg = bits < 0;
        var expBits = (int)((bits >> 52) & 0x7FFL);
        var fracBits = bits & 0x000FFFFFFFFFFFFFL;
        BigInteger mantissa;
        int exp;
        if (expBits == 0)
        {
            mantissa = fracBits;
            exp = -1074;
        }
        else
        {
            mantissa = (1L << 52) | fracBits;
            exp = expBits - 1075;
        }

        string term;
        if (exp >= 0)
        {
            var num = mantissa << exp;
            term = num.ToString(CultureInfo.InvariantCulture) + ".0";
        }
        else
        {
            var denom = BigInteger.One << (-exp);
            term = $"(/ {mantissa.ToString(CultureInfo.InvariantCulture)} {denom.ToString(CultureInfo.InvariantCulture)})";
        }
        return isNeg ? $"(- {term})" : term;
    }

    /// <summary>
    /// Encodes a C# UTF-16 string as an SMT-LIB 2.6 string literal.
    /// Printable ASCII characters (except '"' and '\') are written directly;
    /// '"' is doubled as '""'; '\' and all non-printable or non-ASCII code units
    /// are encoded using SMT-LIB 2.6 \u{hex} escapes.
    /// </summary>
    public static string EscapeSmtString(string text)
    {
        var sb = new StringBuilder(text.Length + 2);
        sb.Append('"');
        foreach (var ch in text)
        {
            if (ch == '"')
            {
                sb.Append("\"\"");
            }
            else if (ch == '\\')
            {
                sb.Append("\\u{5c}");
            }
            else if (ch >= 0x20 && ch <= 0x7E)
            {
                sb.Append(ch);
            }
            else
            {
                sb.Append("\\u{").Append(((int)ch).ToString("x", CultureInfo.InvariantCulture)).Append('}');
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    public static bool IsBitVecSort(string sort) =>
        sort is "(_ BitVec 32)" or "(_ BitVec 64)";

    private static string BitVecToInt(string bvTerm, int width)
    {
        var zero = $"(_ bv0 {width})";
        return $"(ite (bvslt {bvTerm} {zero}) (- (bv2int (bvneg {bvTerm}))) (bv2int {bvTerm}))";
    }
}
