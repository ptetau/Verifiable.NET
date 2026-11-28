using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Verifiable.Analyzers.Smt;

internal sealed partial class CfgSsaLowerer
{
    /// <summary>
    /// Lowers integer <c>/</c> and <c>%</c> with C# meaning, and emits the obligations for the ways they throw.
    /// </summary>
    /// <remarks>
    /// C# division truncates toward zero, and the remainder takes the sign of the dividend: <c>-7 / 2 == -3</c> and
    /// <c>-7 % 2 == -1</c>. SMT-LIB <c>div</c> and <c>mod</c> are Euclidean (<c>-4</c> and <c>1</c>), so on Int they are
    /// applied to the absolute values and the sign is put back. On bit-vectors, <c>bvsdiv</c> and <c>bvsrem</c>
    /// already match C#.
    /// Both throw: <c>DivideByZeroException</c> for a zero divisor, and <c>OverflowException</c> for
    /// <c>MinValue / -1</c> and <c>MinValue % -1</c>, even in unchecked code. The overflow case is only checked under
    /// <c>[CheckedArithmetic]</c>; without it integers are unbounded and have no <c>MinValue</c>.
    /// </remarks>
    private SmtTypedTerm LowerIntegerDivision(
        BinaryOperatorKind kind,
        string lTerm,
        string rTerm,
        string sort,
        ITypeSymbol resultType,
        string gamma,
        Location loc,
        bool emitObligations)
    {
        var isDivide = kind == BinaryOperatorKind.Divide;
        var checkThrows = emitObligations || _inQuantifierDepth > 0;

        if (ContractExpressionTranslator.IsBitVecSort(sort))
        {
            var width = sort == "(_ BitVec 32)" ? 32 : 64;
            if (checkThrows)
            {
                var zero = $"(_ bv0 {width})";
                var minValue = width == 32 ? "(_ bv2147483648 32)" : "(_ bv9223372036854775808 64)";
                var minusOne = $"(bvneg (_ bv1 {width}))";
                EmitSafetyObligation("DivisionByZero", "Divisor must be non-zero", loc, gamma, $"(not (= {rTerm} {zero}))");
                EmitSafetyObligation("Overflow", $"MinValue {(isDivide ? "/" : "%")} -1 throws OverflowException", loc, gamma,
                    $"(not (and (= {lTerm} {minValue}) (= {rTerm} {minusOne})))");
            }

            return new SmtTypedTerm($"({(isDivide ? "bvsdiv" : "bvsrem")} {lTerm} {rTerm})", resultType);
        }

        if (checkThrows)
        {
            EmitSafetyObligation("DivisionByZero", "Divisor must be non-zero", loc, gamma, $"(not (= {rTerm} 0))");
        }

        // A positive literal divisor needs no sign case on the divisor. C# truncation is then floor division of the
        // magnitude with the dividend's sign, which is the same value as the general form below but much easier for
        // Z3 (it avoids `abs` on both sides), so proofs that split numbers into fixed-width digits stay decidable.
        if (System.Numerics.BigInteger.TryParse(rTerm, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var literal) && literal > 0)
        {
            var op = isDivide ? "div" : "mod";

            // A string length is never negative, so the dividend's sign case is dead and plain floor division is exact.
            if (IsStringLengthTerm(lTerm))
            {
                return new SmtTypedTerm($"({op} {lTerm} {rTerm})", resultType);
            }

            return new SmtTypedTerm($"(ite (>= {lTerm} 0) ({op} {lTerm} {rTerm}) (- ({op} (- {lTerm}) {rTerm})))", resultType);
        }

        var magnitude = $"({(isDivide ? "div" : "mod")} (abs {lTerm}) (abs {rTerm}))";
        var negative = isDivide
            ? $"(not (= (>= {lTerm} 0) (>= {rTerm} 0)))"
            : $"(< {lTerm} 0)";
        return new SmtTypedTerm($"(ite {negative} (- {magnitude}) {magnitude})", resultType);
    }

    /// <summary>
    /// C# <c>%</c> on <c>decimal</c> (and on <c>double</c>, which the analyzer models as a real number):
    /// <c>l - r * trunc(l / r)</c>, so <c>-7m % 2m == -1m</c>. The result is exact, so no rounding is lost.
    /// SMT-LIB <c>mod</c> is only defined on Int.
    /// </summary>
    /// <summary>
    /// Whether a term is exactly the lowering of <c>string.Length</c> (zero for null, else the SMT length), which is
    /// never negative. Matched by shape so nothing else is mistaken for it.
    /// </summary>
    private static bool IsStringLengthTerm(string term) =>
        StringLengthShape.IsMatch(term);

    private static readonly System.Text.RegularExpressions.Regex StringLengthShape = new(
        @"^\(ite \(= (?<s>[^\s()]+) str-null\) 0 \(str\.len \(str-text \k<s>\)\)\)$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static string RealRemainder(string lTerm, string rTerm)
    {
        var quotient = $"(/ {lTerm} {rTerm})";
        var truncated = $"(ite (>= {quotient} 0.0) (to_real (to_int {quotient})) (- (to_real (to_int (- {quotient})))))";
        return $"(- {lTerm} (* {rTerm} {truncated}))";
    }
}
