// Math.Sign(int.MinValue) is -1, and -1 * int.MinValue overflows (wraps to int.MinValue), so the product is not always non-negative.
// @expect: rejected
// @witness: Corpus.Arithmetic.R2SignTimes.Run(int.MinValue)
using System;
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class R2SignTimes
{
    public static int Run(int x)
    {
        Spec.Ensures<int>(r => r >= 0, "Non-negative.");
        return Math.Sign(x) * -x;
    }
}
