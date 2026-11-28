// A correct program: the product of two values below 2^32 fits in a ulong.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class R2UlongMul
{
    public static ulong Run(ulong a, ulong b)
    {
        Spec.Requires(a <= 4294967295UL && b <= 4294967295UL, "Both fit in 32 bits.");
        Spec.Ensures<ulong>(r => r >= a || b == 0UL, "At least a unless b is zero.");
        return a * b;
    }
}
