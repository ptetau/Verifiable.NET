// Under [CheckedArithmetic], uint subtraction still wraps: 0u - 1u is uint.MaxValue, not smaller than 0.
// @expect: rejected
// @witness: Corpus.Arithmetic.UintSub.Dec(0u)
// @known-bug: Under [CheckedArithmetic], uint is still an unbounded Int (SmtTypeSystem.GetSort), so uint subtraction never wraps.
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
[CheckedArithmetic]
public static class UintSub
{
    public static uint Dec(uint x)
    {
        Spec.Ensures<uint>(r => r < x, "Result is smaller than the input.");
        return x - 1u;
    }
}
