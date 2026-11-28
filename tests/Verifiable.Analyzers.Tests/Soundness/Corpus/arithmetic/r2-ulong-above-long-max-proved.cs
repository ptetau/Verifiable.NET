// A correct program: a ulong above long.MaxValue casts to a negative long.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class R2UlongCastOk
{
    public static long Run(ulong x)
    {
        Spec.Requires(x > long.MaxValue, "x is above long.MaxValue.");
        Spec.Ensures<long>(r => r < 0, "The cast is negative.");
        return (long)x;
    }
}
