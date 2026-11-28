// A long shift count is masked to 6 bits: 1L << 64 is 1.
// @expect: rejected
// @witness: Corpus.Arithmetic.R2LongShift.Run(64)
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class R2LongShift
{
    public static long Run(int n)
    {
        Spec.Requires(n == 64, "n is 64.");
        Spec.Ensures<long>(r => r == 0, "Shifted out.");
        return 1L << n;
    }
}
