// A ulong above long.MaxValue casts (unchecked) to a negative long, so the result is not positive.
// @expect: rejected
// @witness: Corpus.Arithmetic.R2UlongCast.Run(ulong.MaxValue)
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class R2UlongCast
{
    public static long Run(ulong x)
    {
        Spec.Requires(x > long.MaxValue, "x is above long.MaxValue.");
        Spec.Ensures<long>(r => r > 0, "The cast is positive.");
        return (long)x;
    }
}
