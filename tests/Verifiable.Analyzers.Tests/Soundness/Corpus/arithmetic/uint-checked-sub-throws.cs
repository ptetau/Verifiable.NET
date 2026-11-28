// checked(x - y) on uint throws OverflowException when y > x, even though both are small.
// @expect: rejected
// @witness: Corpus.Arithmetic.UintCheckedSub.Diff(0u, 1u)
// @known-bug: checked uint subtraction gets no overflow obligation: uint is an unbounded Int even under [CheckedArithmetic], and EmitBitVecOverflowObligation only covers int and long.
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
[CheckedArithmetic]
public static class UintCheckedSub
{
    public static uint Diff(uint x, uint y)
    {
        Spec.Requires(x <= 10u && y >= 0u && y <= 10u, "Small inputs.");
        Spec.Ensures<uint>(r => r <= x, "The difference is at most x.");
        return checked(x - y);
    }
}
