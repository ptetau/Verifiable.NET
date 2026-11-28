// A correct program: a shift by -1 is a shift by 63 for long, so a positive value shifts to 0.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class R2NegShift
{
    public static long Run(long x, int n)
    {
        Spec.Requires(x > 0 && n == -1, "Positive value, count -1.");
        Spec.Ensures<long>(r => r == 0, "Positive value shifted by 63.");
        return x >> n;
    }
}
