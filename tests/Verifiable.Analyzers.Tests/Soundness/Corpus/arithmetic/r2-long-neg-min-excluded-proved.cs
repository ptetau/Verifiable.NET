// A correct program: negating a long other than long.MinValue is in range, and Math.Abs agrees with it.
// @expect: proved
using System;
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class R2LongNeg
{
    public static long Run(long x)
    {
        Spec.Requires(x != long.MinValue, "Negation is in range.");
        Spec.Ensures<long>(r => r >= 0 && (r == x || r == -x), "The absolute value.");
        return x < 0 ? -x : Math.Abs(x);
    }
}
