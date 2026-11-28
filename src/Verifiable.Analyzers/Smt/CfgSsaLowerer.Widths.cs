using System;
using System.Globalization;
using System.Numerics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Verifiable.Analyzers.Smt;

/// <summary>
/// Integer widths as C# has them (#27): bitwise operators and shifts, narrowing conversions, and arithmetic on the
/// types narrower than <c>int</c>.
/// </summary>
/// <remarks>
/// In the default mode every C# integer type is a Z3 <c>Int</c> that is assumed to lie in the type's range. Anything
/// that needs the bits (<c>&amp; | ^ ~ &lt;&lt; &gt;&gt;</c> with a non-constant operand) goes through a bit-vector of
/// the type's width, read back as signed or unsigned as the type is (<c>bv2int</c>). The Int is tied to its
/// bit-vector by a fresh bit-vector constant and an equation (see <c>ToBitVec</c>), because Z3 often gives up on
/// <c>int2bv</c>; a value that is itself the read-back of a bit-vector (the result of <c>&amp;</c>, a narrowing of it)
/// is used as that bit-vector, so a chain of bit operations never leaves the bits. Under <c>[CheckedArithmetic]</c>,
/// <c>int</c> and <c>long</c> already are bit-vectors and use the bit-vector operators directly; <c>uint</c>,
/// <c>ulong</c> and the types narrower than <c>int</c> stay bounded <c>Int</c>s there too (wrapping, as bit-vectors
/// do), so a term of one of those types never has a bit-vector sort.
/// </remarks>
internal sealed partial class CfgSsaLowerer
{
    /// <summary>
    /// An operation the compiler has already evaluated (<c>1 &lt;&lt; 4</c>, <c>~0</c>, <c>Read | Write</c>,
    /// <c>unchecked((byte)300)</c>) is that constant, with no operator to model.
    /// </summary>
    private bool TryFoldIntegralConstant(IOperation op, out SmtTypedTerm folded)
    {
        folded = default!;
        if (!op.ConstantValue.HasValue || op.ConstantValue.Value is null || op.Type is null)
            return false;
        if (op.Type.TypeKind != TypeKind.Enum && !IsPlainIntegral(op.Type, out _, out _, out _, out _))
            return false;
        folded = FormatConstantScalarValue(op.ConstantValue.Value, op.Type);
        return true;
    }

    // ---- operations that throw, inside a contract -----------------------------------------------------------

    /// <summary>
    /// Set while a contract is lowered as something to prove (a postcondition, a callee's precondition at a call, a
    /// loop invariant). Evaluating that expression at run time throws on a zero divisor, so the division must carry
    /// its obligation there too. A contract assumed on entry is not checked this way: the caller proved it.
    /// </summary>
    private bool _checkSpecThrows;

    private bool ChecksThrowingOperations(bool emitObligations) =>
        emitObligations || _inQuantifierDepth > 0 || _checkSpecThrows;

    private SmtTypedTerm LowerSpecGoal(IOperation op, string gamma, BlockEnv env)
    {
        var previous = _checkSpecThrows;
        _checkSpecThrows = true;
        try
        {
            return LowerExpression(op, gamma, env, emitObligations: false);
        }
        finally
        {
            _checkSpecThrows = previous;
        }
    }

    // ---- a cheap interval for an Int term --------------------------------------------------------------------

    /// <summary>The range of terms that are known to hold a value of their C# type (parameters, fields, call results).</summary>
    private readonly System.Collections.Generic.Dictionary<string, (BigInteger Min, BigInteger Max)> _termBounds = new(StringComparer.Ordinal);

    private sealed record BoundNode(string Text, System.Collections.Generic.List<BoundNode>? Children);

    private static BoundNode? ParseBoundNode(string text, ref int pos)
    {
        while (pos < text.Length && text[pos] == ' ') pos++;
        if (pos >= text.Length) return null;
        var start = pos;
        if (text[pos] != '(')
        {
            while (pos < text.Length && text[pos] != ' ' && text[pos] != ')' && text[pos] != '(') pos++;
            return new BoundNode(text.Substring(start, pos - start), null);
        }

        pos++;
        var children = new System.Collections.Generic.List<BoundNode>();
        while (true)
        {
            while (pos < text.Length && text[pos] == ' ') pos++;
            if (pos >= text.Length) return null;
            if (text[pos] == ')') { pos++; break; }
            var child = ParseBoundNode(text, ref pos);
            if (child is null) return null;
            children.Add(child);
        }

        return new BoundNode(text.Substring(start, pos - start), children);
    }

    private bool TryBoundNode(BoundNode node, out BigInteger lo, out BigInteger hi)
    {
        lo = hi = BigInteger.Zero;
        if (node.Children is null)
        {
            if (TryParseNumeral(node.Text, out lo)) { hi = lo; return true; }
            if (_termBounds.TryGetValue(node.Text, out var known)) { (lo, hi) = known; return true; }
            return false;
        }

        if (IsStringLengthTerm(node.Text))
        {
            lo = 0;
            hi = 1073741791;
            return true;
        }

        if (TryParseNumeral(node.Text, out lo)) { hi = lo; return true; }
        if (_termBounds.TryGetValue(node.Text, out var knownTerm)) { (lo, hi) = knownTerm; return true; }

        var c = node.Children;
        if (c.Count == 0 || c[0].Children is not null) return false;
        var head = c[0].Text;
        var args = new System.Collections.Generic.List<(BigInteger Lo, BigInteger Hi)>();
        if (head is not ("ite" or "mod"))
        {
            for (var i = 1; i < c.Count; i++)
            {
                if (!TryBoundNode(c[i], out var l, out var h)) return false;
                args.Add((l, h));
            }
        }

        switch (head)
        {
            case "ite" when c.Count == 4:
                if (!TryBoundNode(c[2], out var l2, out var h2) || !TryBoundNode(c[3], out var l3, out var h3)) return false;
                lo = BigInteger.Min(l2, l3);
                hi = BigInteger.Max(h2, h3);
                return true;
            case "mod" when c.Count == 3 && TryParseNumeral(c[2].Text, out var m) && m > 0:
                lo = 0;
                hi = m - 1;
                if (TryBoundNode(c[1], out var ml, out var mh) && ml >= 0 && mh < m) { lo = ml; hi = mh; }
                return true;
            case "div" when args.Count == 2 && args[1].Lo == args[1].Hi && args[1].Lo > 0:
                lo = FloorDiv(args[0].Lo, args[1].Lo);
                hi = FloorDiv(args[0].Hi, args[1].Lo);
                return true;
            case "+" when args.Count > 0:
                foreach (var a in args) { lo += a.Lo; hi += a.Hi; }
                return true;
            case "-" when args.Count == 1:
                lo = -args[0].Hi;
                hi = -args[0].Lo;
                return true;
            case "-" when args.Count == 2:
                lo = args[0].Lo - args[1].Hi;
                hi = args[0].Hi - args[1].Lo;
                return true;
            case "*" when args.Count == 2:
                var products = new[] { args[0].Lo * args[1].Lo, args[0].Lo * args[1].Hi, args[0].Hi * args[1].Lo, args[0].Hi * args[1].Hi };
                lo = products[0];
                hi = products[0];
                foreach (var pr in products) { lo = BigInteger.Min(lo, pr); hi = BigInteger.Max(hi, pr); }
                return true;
            default:
                return false;
        }
    }

    private static BigInteger FloorDiv(BigInteger a, BigInteger b)
    {
        var q = BigInteger.DivRem(a, b, out var r);
        return r.Sign < 0 ? q - 1 : q;
    }

    /// <summary>Whether the value of <paramref name="intTerm"/> is known, from its shape alone, to lie in [min, max].</summary>
    private bool TermFitsRange(string intTerm, BigInteger min, BigInteger max)
    {
        if (intTerm.IndexOf('"') >= 0)
            return false;
        var pos = 0;
        var node = ParseBoundNode(intTerm, ref pos);
        return node is not null && TryBoundNode(node, out var lo, out var hi) && lo >= min && hi <= max;
    }

    private static bool IsBitwiseOrShift(BinaryOperatorKind kind) =>
        kind is BinaryOperatorKind.And or BinaryOperatorKind.Or or BinaryOperatorKind.ExclusiveOr or
            BinaryOperatorKind.LeftShift or BinaryOperatorKind.RightShift or BinaryOperatorKind.UnsignedRightShift;

    private static bool IsShift(BinaryOperatorKind kind) =>
        kind is BinaryOperatorKind.LeftShift or BinaryOperatorKind.RightShift or BinaryOperatorKind.UnsignedRightShift;

    /// <summary>An integer literal of <paramref name="type"/>'s sort: a bit-vector literal for a bit-vector type, else an Int.</summary>
    private string IntegerLiteralOfType(ITypeSymbol type, BigInteger value)
    {
        var sort = _types.GetSort(type);
        if (ContractExpressionTranslator.IsBitVecSort(sort))
        {
            var width = sort == "(_ BitVec 32)" ? 32 : 64;
            var unsigned = value & ((BigInteger.One << width) - 1);
            return $"(_ bv{unsigned.ToString(CultureInfo.InvariantCulture)} {width})";
        }

        return CalendarArithmetic.FormatBigInt(value);
    }

    /// <summary>Whether <paramref name="type"/> is an integer type that is not an enum, and gives its bounds.</summary>
    private static bool IsPlainIntegral(ITypeSymbol? type, out BigInteger min, out BigInteger max, out int bits, out bool signed)
    {
        min = max = BigInteger.Zero;
        bits = 0;
        signed = false;
        return type is not null && type.TypeKind != TypeKind.Enum && TryGetIntegralBounds(type, out min, out max, out bits, out signed);
    }

    private static bool TryParseNumeral(string term, out BigInteger value)
    {
        if (BigInteger.TryParse(term, NumberStyles.None, CultureInfo.InvariantCulture, out value))
            return true;
        if (term.StartsWith("(- ", StringComparison.Ordinal) && term.EndsWith(")", StringComparison.Ordinal) &&
            BigInteger.TryParse(term.Substring(3, term.Length - 4), NumberStyles.None, CultureInfo.InvariantCulture, out var magnitude))
        {
            value = -magnitude;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// The value of <paramref name="intTerm"/> as the integer type with the given range, wrapping modulo 2^bits when it
    /// is outside. A value already in range is left alone (an <c>ite</c> guard), which keeps proofs that never wrap cheap.
    /// </summary>
    private string WrapIntegerTerm(string intTerm, BigInteger min, BigInteger max, int bits)
    {
        if (TermFitsRange(intTerm, min, max))
            return intTerm;

        var modulus = BigInteger.One << bits;
        var minStr = CalendarArithmetic.FormatBigInt(min);
        var maxStr = CalendarArithmetic.FormatBigInt(max);
        var wrapped = $"(+ (mod (- {intTerm} {minStr}) {CalendarArithmetic.FormatBigInt(modulus)}) {minStr})";
        return $"(ite (and (<= {minStr} {intTerm}) (<= {intTerm} {maxStr})) {intTerm} {wrapped})";
    }

    // ---- bit operations -------------------------------------------------------------------------------------

    private readonly System.Collections.Generic.Dictionary<string, string> _bitVecLinks = new(StringComparer.Ordinal);

    /// <summary>
    /// The bit-vector of <paramref name="bits"/> bits that holds <paramref name="intTerm"/>, an Int known to lie in
    /// [<paramref name="min"/>, <paramref name="max"/>]. A fresh bit-vector constant is tied to the Int by
    /// <c>bv2int</c>, which Z3 decides at once where it often gives up on <c>int2bv</c>. The tie only holds for an
    /// Int in range, so a term outside it leaves the constant free rather than contradicting anything.
    /// </summary>
    private string ToBitVec(string intTerm, int bits, BigInteger min, BigInteger max)
    {
        if (TryParseNumeral(intTerm, out var literal))
            return $"(_ bv{(literal & ((BigInteger.One << bits) - 1)).ToString(CultureInfo.InvariantCulture)} {bits})";

        if (_inQuantifierDepth > 0)
            return $"((_ int2bv {bits}) {intTerm})";

        var signed = min.Sign < 0;
        if (_intBitVecOrigin.TryGetValue(intTerm, out var origin))
        {
            if (origin.Bits == bits && origin.Signed == signed)
                return origin.BitVec;

            // A narrower bit-vector widens without changing the value, when the value fits the wider type.
            if (origin.Bits < bits && (origin.Signed == signed || !origin.Signed && signed))
                return $"((_ {(origin.Signed ? "sign_extend" : "zero_extend")} {bits - origin.Bits}) {origin.BitVec})";
        }

        var key = $"{bits}|{signed}|{intTerm}";
        if (_bitVecLinks.TryGetValue(key, out var existing))
            return existing;

        var name = $"bv_{++_ssaCounter}_link";
        _decls.Add($"(declare-const {name} (_ BitVec {bits}))");
        var inRange = $"(and (<= {CalendarArithmetic.FormatBigInt(min)} {intTerm}) (<= {intTerm} {CalendarArithmetic.FormatBigInt(max)}))";
        AddAssertion($"(=> {inRange} (= {intTerm} {FromBitVec(name, bits, signed)}))", "BitVecLink", "int and bit-vector views agree", Location.None);
        _bitVecLinks[key] = name;
        return name;
    }

    /// <summary>The Int terms that are a bit-vector read back, with that bit-vector: a chain of bit operations stays in bit-vectors.</summary>
    private readonly System.Collections.Generic.Dictionary<string, (string BitVec, int Bits, bool Signed)> _intBitVecOrigin = new(StringComparer.Ordinal);

    private string FromBitVec(string bvTerm, int bits, bool signed)
    {
        string intTerm;
        if (!signed)
        {
            intTerm = $"(bv2int {bvTerm})";
        }
        else
        {
            var half = CalendarArithmetic.FormatBigInt(BigInteger.One << (bits - 1));
            var modulus = CalendarArithmetic.FormatBigInt(BigInteger.One << bits);
            intTerm = $"(let ((_bw (bv2int {bvTerm}))) (ite (>= _bw {half}) (- _bw {modulus}) _bw))";
        }

        _intBitVecOrigin[intTerm] = (bvTerm, bits, signed);
        return intTerm;
    }

    /// <summary>A name bound to an Int that is a bit-vector read back is that same bit-vector.</summary>
    private void PropagateBitVecOrigin(string intTerm, string name)
    {
        if (_intBitVecOrigin.TryGetValue(intTerm, out var origin))
            _intBitVecOrigin[name] = origin;
    }

    /// <summary>
    /// Lowers <c>&amp;</c>, <c>|</c>, <c>^</c>, <c>&lt;&lt;</c>, <c>&gt;&gt;</c> and <c>&gt;&gt;&gt;</c> on integers (and
    /// enums, by their underlying type) and the Boolean forms of the first three. Returns false for a type it has no
    /// model for.
    /// </summary>
    private bool TryLowerBitwiseOperator(
        BinaryOperatorKind kind,
        string lTerm,
        string rTerm,
        string sort,
        ITypeSymbol operandType,
        ITypeSymbol resultType,
        out SmtTypedTerm result)
    {
        result = default!;

        if (sort == "Bool")
        {
            var op = kind switch
            {
                BinaryOperatorKind.And => "and",
                BinaryOperatorKind.Or => "or",
                BinaryOperatorKind.ExclusiveOr => "xor",
                _ => null,
            };
            if (op is null) return false;
            result = new SmtTypedTerm($"({op} {lTerm} {rTerm})", resultType);
            return true;
        }

        if (!TryGetIntegralBounds(operandType, out var min, out var max, out var bits, out var signed))
            return false;

        string term;
        if (ContractExpressionTranslator.IsBitVecSort(sort))
        {
            // int and long under [CheckedArithmetic]: the terms already are bit-vectors of the right width.
            var width = sort == "(_ BitVec 32)" ? 32 : 64;
            if (width != bits) return false;
            string count() => $"(bvand {rTerm} (_ bv{width - 1} {width}))";
            term = kind switch
            {
                BinaryOperatorKind.And => $"(bvand {lTerm} {rTerm})",
                BinaryOperatorKind.Or => $"(bvor {lTerm} {rTerm})",
                BinaryOperatorKind.ExclusiveOr => $"(bvxor {lTerm} {rTerm})",
                BinaryOperatorKind.LeftShift => $"(bvshl {lTerm} {count()})",
                BinaryOperatorKind.RightShift => $"(bvashr {lTerm} {count()})",
                BinaryOperatorKind.UnsignedRightShift => $"(bvlshr {lTerm} {count()})",
                _ => throw new InvalidOperationException(),
            };
            result = new SmtTypedTerm(term, resultType);
            return true;
        }

        if (sort != "Int") return false;

        var modulus = BigInteger.One << bits;
        if (!IsShift(kind))
        {
            // x ^ x, x & x, x | x, and the zero cases need no bit-vector at all.
            var lZero = TryParseNumeral(lTerm, out var lValue) && lValue.IsZero;
            var rZero = TryParseNumeral(rTerm, out var rValue) && rValue.IsZero;
            if (kind == BinaryOperatorKind.ExclusiveOr && lTerm == rTerm)
            {
                result = new SmtTypedTerm("0", resultType);
                return true;
            }

            if (kind is BinaryOperatorKind.And or BinaryOperatorKind.Or && lTerm == rTerm)
            {
                result = new SmtTypedTerm(lTerm, resultType);
                return true;
            }

            if (kind == BinaryOperatorKind.And && (lZero || rZero))
            {
                result = new SmtTypedTerm("0", resultType);
                return true;
            }

            if (kind is BinaryOperatorKind.Or or BinaryOperatorKind.ExclusiveOr && (lZero || rZero))
            {
                result = new SmtTypedTerm(lZero ? rTerm : lTerm, resultType);
                return true;
            }

            // All ones: -1 on a signed type, 2^n - 1 on an unsigned one.
            var allOnes = signed ? BigInteger.MinusOne : modulus - 1;
            var lOnes = TryParseNumeral(lTerm, out var lOnesValue) && lOnesValue == allOnes;
            var rOnes = TryParseNumeral(rTerm, out var rOnesValue) && rOnesValue == allOnes;
            if (kind == BinaryOperatorKind.And && (lOnes || rOnes))
            {
                result = new SmtTypedTerm(lOnes ? rTerm : lTerm, resultType);
                return true;
            }

            if (kind == BinaryOperatorKind.Or && (lOnes || rOnes))
            {
                result = new SmtTypedTerm(CalendarArithmetic.FormatBigInt(allOnes), resultType);
                return true;
            }
        }

        if (IsShift(kind))
        {
            if (TryParseNumeral(rTerm, out var rawCount))
            {
                var c = (int)(((rawCount % bits) + bits) % bits);
                if (c == 0)
                {
                    result = new SmtTypedTerm(lTerm, resultType);
                    return true;
                }

                var scale = CalendarArithmetic.FormatBigInt(BigInteger.One << c);
                switch (kind)
                {
                    case BinaryOperatorKind.LeftShift:
                        // x << c is x * 2^c reduced into the type's range.
                        result = new SmtTypedTerm(WrapIntegerTerm($"(* {lTerm} {scale})", min, max, bits), resultType);
                        return true;
                    case BinaryOperatorKind.RightShift:
                        // Arithmetic shift (or a logical one on an unsigned type) rounds toward negative infinity.
                        result = new SmtTypedTerm($"(div {lTerm} {scale})", resultType);
                        return true;
                    default:
                        // Logical shift of a signed type: shift the unsigned bit pattern; c > 0 keeps it in range.
                        result = new SmtTypedTerm(signed
                            ? $"(div (mod {lTerm} {CalendarArithmetic.FormatBigInt(modulus)}) {scale})"
                            : $"(div {lTerm} {scale})", resultType);
                        return true;
                }
            }

            var count = $"(mod {rTerm} {bits})";
            var lbv = ToBitVec(lTerm, bits, min, max);
            var cbv = ToBitVec(count, bits, BigInteger.Zero, bits - 1);
            var shifted = kind switch
            {
                BinaryOperatorKind.LeftShift => $"(bvshl {lbv} {cbv})",
                BinaryOperatorKind.RightShift => signed ? $"(bvashr {lbv} {cbv})" : $"(bvlshr {lbv} {cbv})",
                _ => $"(bvlshr {lbv} {cbv})",
            };
            result = new SmtTypedTerm(FromBitVec(shifted, bits, signed), resultType);
            return true;
        }

        // x & (2^k - 1) keeps the low k bits, which is x modulo 2^k whatever the sign of x.
        if (kind == BinaryOperatorKind.And)
        {
            foreach (var (value, other) in new[] { (rTerm, lTerm), (lTerm, rTerm) })
            {
                if (TryParseNumeral(value, out var mask) && mask >= 0 && mask < modulus && ((mask + 1) & mask).IsZero)
                {
                    var k = 0;
                    for (var p = mask + 1; p > 1; p >>= 1) k++;
                    result = new SmtTypedTerm(k == 0 ? "0" : $"(mod {other} {CalendarArithmetic.FormatBigInt(BigInteger.One << k)})", resultType);
                    return true;
                }
            }
        }

        var bvOp = kind switch
        {
            BinaryOperatorKind.And => "bvand",
            BinaryOperatorKind.Or => "bvor",
            _ => "bvxor",
        };
        result = new SmtTypedTerm(FromBitVec($"({bvOp} {ToBitVec(lTerm, bits, min, max)} {ToBitVec(rTerm, bits, min, max)})", bits, signed), resultType);
        return true;
    }

    /// <summary><c>~x</c>: <c>-x - 1</c> on a signed type, <c>2^n - 1 - x</c> on an unsigned one.</summary>
    private SmtTypedTerm LowerBitwiseComplement(SmtTypedTerm operand, ITypeSymbol resultType)
    {
        operand = _translator.UnwrapRefinedIfApplicable(operand);
        var sort = _types.GetSort(operand.Type);
        if (sort == "(_ BitVec 32)" || sort == "(_ BitVec 64)")
            return new SmtTypedTerm($"(bvnot {operand.Term})", resultType);

        if (sort == "Int" && TryGetIntegralBounds(operand.Type, out var notMin, out var max, out var notBits, out var signed))
        {
            if (operand.Type.TypeKind != TypeKind.Enum && _intBitVecOrigin.ContainsKey(operand.Term))
            {
                var bv = ToBitVec(operand.Term, notBits, notMin, max);
                return new SmtTypedTerm(FromBitVec($"(bvnot {bv})", notBits, signed), resultType);
            }

            var term = signed
                ? $"(- (- {operand.Term}) 1)"
                : $"(- {CalendarArithmetic.FormatBigInt(max)} {operand.Term})";
            return new SmtTypedTerm(term, resultType);
        }

        throw new ContractLoweringException($"Unsupported operand type '{operand.Type}' for '~'.");
    }

    // ---- narrowing conversions ------------------------------------------------------------------------------

    /// <summary>A narrow or unsigned Int value converted to <c>int</c> or <c>long</c> under <c>[CheckedArithmetic]</c>.</summary>
    private bool TryWidenIntToBitVec(SmtTypedTerm inner, ITypeSymbol target, out SmtTypedTerm converted)
    {
        converted = default!;
        var dstSort = _types.GetSort(target);
        if (_types.GetSort(inner.Type) != "Int" || !ContractExpressionTranslator.IsBitVecSort(dstSort) || inner.Type.TypeKind == TypeKind.Enum ||
            !TryGetIntegralBounds(inner.Type, out var min, out var max, out _, out _))
            return false;
        var width = dstSort == "(_ BitVec 32)" ? 32 : 64;
        var lowest = -(BigInteger.One << (width - 1));
        var highest = (BigInteger.One << (width - 1)) - 1;
        if (min < lowest || max > highest)
            return false;
        converted = new SmtTypedTerm(ToBitVec(inner.Term, width, min, max), target);
        return true;
    }

    /// <summary>
    /// A conversion from a real number (<c>decimal</c>, <c>double</c>, <c>float</c>) to an integer type. C# truncates
    /// toward zero, and a <c>decimal</c> that does not fit throws, as does any real in a <c>checked</c> context.
    /// </summary>
    private bool TryLowerRealToIntegral(
        SmtTypedTerm inner,
        ITypeSymbol target,
        bool isChecked,
        string gamma,
        Location loc,
        out SmtTypedTerm converted)
    {
        converted = default!;
        if (_types.GetSort(inner.Type) != "Real" || !TryGetIntegralBounds(target, out var min, out var max, out var bits, out _))
            return false;

        var truncated = ContractExpressionTranslator.TruncateToInt(inner.Term);
        if (inner.Type.SpecialType == SpecialType.System_Decimal || isChecked)
        {
            EmitSafetyObligation("Overflow", $"Conversion of '{inner.Type.Name}' to '{target.Name}' must stay within target range", loc, gamma,
                $"(and (>= {truncated} {CalendarArithmetic.FormatBigInt(min)}) (<= {truncated} {CalendarArithmetic.FormatBigInt(max)}))");
            converted = new SmtTypedTerm(AsSortOf(target, truncated), target);
            return true;
        }

        // An unchecked double or float out of range has an unspecified result; the model wraps it like an integer would.
        converted = new SmtTypedTerm(AsSortOf(target, WrapIntegerTerm(truncated, min, max, bits)), target);
        return true;
    }

    /// <summary>An Int-valued term as a term of <paramref name="type"/>'s sort.</summary>
    private string AsSortOf(ITypeSymbol type, string intTerm) => _types.GetSort(type) switch
    {
        "(_ BitVec 32)" => $"((_ int2bv 32) {intTerm})",
        "(_ BitVec 64)" => $"((_ int2bv 64) {intTerm})",
        _ => intTerm,
    };

    /// <summary>
    /// An unchecked narrowing conversion of a bit-vector (<c>int</c> or <c>long</c> under <c>[CheckedArithmetic]</c>):
    /// the low bits, read back as signed or unsigned. Done on the bits, Z3 decides it far more easily than through the
    /// Int conversion.
    /// </summary>
    private bool TryNarrowBitVector(SmtTypedTerm inner, ITypeSymbol target, int bits, bool signed, out SmtTypedTerm converted)
    {
        converted = default!;
        // An Int that is a bit-vector read back (the result of `&`, `<<`, ...) narrows on the bits, staying a bit-vector.
        if (_types.GetSort(target) == "Int" && _intBitVecOrigin.TryGetValue(inner.Term, out var origin) && bits <= origin.Bits)
        {
            var bitsLow = bits == origin.Bits ? origin.BitVec : $"((_ extract {bits - 1} 0) {origin.BitVec})";
            converted = new SmtTypedTerm(FromBitVec(bitsLow, bits, signed), target);
            return true;
        }

        var srcSort = _types.GetSort(inner.Type);
        if (!ContractExpressionTranslator.IsBitVecSort(srcSort))
            return false;
        var width = srcSort == "(_ BitVec 32)" ? 32 : 64;
        if (bits > width)
            return false;

        var low = bits == width ? inner.Term : $"((_ extract {bits - 1} 0) {inner.Term})";
        var dstSort = _types.GetSort(target);
        if (ContractExpressionTranslator.IsBitVecSort(dstSort))
        {
            converted = new SmtTypedTerm(low, target);
            return true;
        }

        converted = new SmtTypedTerm(FromBitVec(low, bits, signed), target);
        return true;
    }

    // ---- compound assignment and ++ / -- --------------------------------------------------------------------

    /// <summary>
    /// The type the arithmetic of <c>x op= y</c> is done in: C# promotes <c>sbyte</c>, <c>byte</c>, <c>short</c>,
    /// <c>ushort</c> and <c>char</c> operands to <c>int</c>, so <c>b += 1</c> on a byte adds in <c>int</c> and
    /// converts the sum back to <c>byte</c> (<c>OutConversion</c>).
    /// </summary>
    private ITypeSymbol? PromotedOperationType(BinaryOperatorKind kind, ITypeSymbol target, IOperation? value)
    {
        var intType = _compilation.GetSpecialType(SpecialType.System_Int32);
        var longType = _compilation.GetSpecialType(SpecialType.System_Int64);
        ITypeSymbol Promote(ITypeSymbol t) => t.SpecialType switch
        {
            SpecialType.System_SByte or SpecialType.System_Byte or SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Char => intType,
            _ => t,
        };

        if (IsShift(kind) || value is null)
            return Promote(target);

        // The right operand may carry an implicit conversion to the operator's parameter type; look at what it converts.
        while (value is IConversionOperation { IsImplicit: true } c && c.Operand.Type is not null)
            value = c.Operand;

        var valueType = value.Type;
        if (valueType is null || !IsPlainIntegral(valueType, out _, out _, out _, out _))
            return null;

        // A constant that fits the target converts to it, so `u += 1` on a uint adds in uint, not in long.
        if (value.ConstantValue.HasValue && value.ConstantValue.Value is not null &&
            TryGetIntegralBounds(target, out var tMin, out var tMax, out _, out _) &&
            ConvertToBigInteger(value.ConstantValue.Value) is var cv && cv >= tMin && cv <= tMax)
        {
            valueType = target;
        }

        // Binary numeric promotion of two integer types.
        var ls = target.SpecialType;
        var rs = valueType.SpecialType;
        static bool IsUnsigned(SpecialType t) =>
            t is SpecialType.System_UInt64 or SpecialType.System_UInt32 or SpecialType.System_UInt16 or SpecialType.System_Byte or SpecialType.System_Char;
        if (ls == SpecialType.System_UInt64 || rs == SpecialType.System_UInt64)
            return IsUnsigned(ls) && IsUnsigned(rs) ? _compilation.GetSpecialType(SpecialType.System_UInt64) : null;
        if (ls == SpecialType.System_Int64 || rs == SpecialType.System_Int64)
            return longType;
        if (ls == SpecialType.System_UInt32 || rs == SpecialType.System_UInt32)
            return IsUnsigned(ls) && IsUnsigned(rs) ? _compilation.GetSpecialType(SpecialType.System_UInt32) : longType;
        return intType;
    }

    /// <summary>
    /// <c>x op= y</c> and <c>x++</c> on an integer type: the operation in the promoted type, then the conversion
    /// back to the target's type. Returns false when the target is not an integer type (the caller uses its general
    /// path).
    /// </summary>
    /// <summary>An operand of an integer operation as the operation's type, tied to a bit-vector where that type is one.</summary>
    private SmtTypedTerm ConvertIntegralOperand(SmtTypedTerm value, ITypeSymbol type) =>
        TryWidenIntToBitVec(value, type, out var widened) ? widened : _translator.ConvertTo(value, type);

    private static bool TryGetConstantOperand(SmtTypedTerm? rhs, IOperation? rhsOperation, out string constantText)
    {
        constantText = "1";
        if (rhs is null)
            return true;
        if (rhsOperation is { ConstantValue: { HasValue: true, Value: { } value } } && IsPlainIntegral(rhsOperation.Type, out _, out _, out _, out _))
        {
            constantText = CalendarArithmetic.FormatBigInt(ConvertToBigInteger(value));
            return true;
        }

        return false;
    }

    private bool TryLowerIntegralUpdate(
        BinaryOperatorKind kind,
        ITypeSymbol targetType,
        SmtTypedTerm current,
        SmtTypedTerm? rhs,
        IOperation? rhsOperation,
        bool checkOverflow,
        string gamma,
        Location loc,
        bool emitObligations,
        out SmtTypedTerm result)
    {
        result = default!;
        if (!IsPlainIntegral(targetType, out _, out _, out _, out _))
            return false;

        var promoted = PromotedOperationType(kind, targetType, rhsOperation);
        if (promoted is null || !IsPlainIntegral(promoted, out _, out _, out _, out _))
            return false;

        // Under [CheckedArithmetic] a byte, short or char is an Int and `int` is a bit-vector. A sum or difference of a
        // narrow value and a constant (`b += 1`, `s--`) cannot overflow `int`, so it is done on Int and converted back
        // once, without a round trip through bit-vectors.
        if (_types.UseBitVec && promoted.SpecialType == SpecialType.System_Int32 && kind is BinaryOperatorKind.Add or BinaryOperatorKind.Subtract &&
            _types.GetSort(targetType) == "Int" && targetType.SpecialType != SpecialType.System_UInt32 &&
            TryGetIntegralBounds(targetType, out var narrowMin, out var narrowMax, out var narrowBits, out _) && narrowBits <= 16 &&
            TryGetConstantOperand(rhs, rhsOperation, out var constantText))
        {
            var exact = $"({(kind == BinaryOperatorKind.Add ? "+" : "-")} {current.Term} {constantText})";
            if (checkOverflow)
            {
                EmitSafetyObligation("Overflow", $"Checked conversion to '{targetType.Name}' must stay within target range", loc, gamma,
                    $"(and (>= {exact} {CalendarArithmetic.FormatBigInt(narrowMin)}) (<= {exact} {CalendarArithmetic.FormatBigInt(narrowMax)}))");
            }

            // Bit-vector arithmetic wraps whether or not the overflow was refuted, and so do these.
            result = new SmtTypedTerm(WrapIntegerTerm(exact, narrowMin, narrowMax, narrowBits), targetType);
            return true;
        }

        var left = ConvertIntegralOperand(current, promoted);
        SmtTypedTerm right;
        if (rhs is null)
        {
            right = new SmtTypedTerm(IntegerLiteralOfType(promoted, BigInteger.One), promoted);
        }
        else if (IsShift(kind))
        {
            right = rhs;
        }
        else
        {
            right = ConvertIntegralOperand(rhs, promoted);
        }

        var operated = LowerBinaryOperator(kind, left, right, promoted, gamma, loc, emitObligations, checkOverflow);
        if (SymbolEqualityComparer.Default.Equals(promoted, targetType))
        {
            result = new SmtTypedTerm(operated.Term, targetType);
            return true;
        }

        operated = new SmtTypedTerm(operated.Term, promoted);
        if (!TryLowerIntegralOrEnumConversion(operated, targetType, checkOverflow, gamma, loc, out var narrowed))
            narrowed = _translator.ConvertTo(operated, targetType);
        result = narrowed;
        return true;
    }

    // ---- Math intrinsics ------------------------------------------------------------------------------------

    /// <summary><c>Math.Clamp</c> and <c>Math.Sign</c> (the other intrinsics are in <c>LowerInvocation</c>).</summary>
    private bool TryLowerMathWidthIntrinsic(
        IMethodSymbol method,
        System.Collections.Generic.IReadOnlyList<SmtTypedTerm> args,
        string gamma,
        Location loc,
        bool emitObligations,
        out SmtTypedTerm result)
    {
        result = default!;
        if (method.ContainingType?.ToDisplayString() != "System.Math")
            return false;

        if (method.Name == "Clamp" && args.Count == 3)
        {
            var value = _translator.UnwrapRefinedIfApplicable(args[0]);
            var lo = _translator.UnwrapRefinedIfApplicable(args[1]);
            var hi = _translator.UnwrapRefinedIfApplicable(args[2]);
            var sort = _types.GetSort(value.Type);
            if (_types.GetSort(lo.Type) != sort || _types.GetSort(hi.Type) != sort)
                return false;
            var isBv = ContractExpressionTranslator.IsBitVecSort(sort);
            var lt = isBv ? "bvslt" : "<";
            var gt = isBv ? "bvsgt" : ">";
            if (emitObligations || _inQuantifierDepth > 0)
            {
                // Math.Clamp throws ArgumentException when min > max.
                EmitSafetyObligation("Precondition", "Math.Clamp requires min <= max", loc, gamma, $"(not ({gt} {lo.Term} {hi.Term}))");
            }

            result = new SmtTypedTerm($"(ite ({lt} {value.Term} {lo.Term}) {lo.Term} (ite ({gt} {value.Term} {hi.Term}) {hi.Term} {value.Term}))", method.ReturnType);
            return true;
        }

        if (method.Name == "Sign" && args.Count == 1)
        {
            var a = _translator.UnwrapRefinedIfApplicable(args[0]);
            var sort = _types.GetSort(a.Type);
            var isBv = ContractExpressionTranslator.IsBitVecSort(sort);
            var zero = sort == "Real" ? "0.0" : isBv ? $"(_ bv0 {(sort == "(_ BitVec 32)" ? 32 : 64)})" : "0";
            var intType = _compilation.GetSpecialType(SpecialType.System_Int32);
            var minusOne = IntegerLiteralOfType(intType, BigInteger.MinusOne);
            var plusOne = IntegerLiteralOfType(intType, BigInteger.One);
            var none = IntegerLiteralOfType(intType, BigInteger.Zero);
            result = new SmtTypedTerm($"(ite ({(isBv ? "bvslt" : "<")} {a.Term} {zero}) {minusOne} (ite ({(isBv ? "bvsgt" : ">")} {a.Term} {zero}) {plusOne} {none}))", method.ReturnType);
            return true;
        }

        return false;
    }
}
