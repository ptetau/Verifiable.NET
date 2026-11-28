// (uint)-1 is uint.MaxValue, so the result can be far above 10 even under [CheckedArithmetic].
// @expect: rejected
// @witness: Corpus.Arithmetic.UintOfNegative.Reinterpret(-1)
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
[CheckedArithmetic]
public static class UintOfNegative
{
    public static uint Reinterpret(int x)
    {
        Spec.Requires(x >= -5 && x < 10, "A small input.");
        Spec.Ensures<uint>(r => r < 10u, "The result stays small.");
        return (uint)x;
    }
}
