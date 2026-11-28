// Math.Abs(long.MinValue) throws even under [UncheckedArithmetic].
// @expect: rejected
// @witness: Corpus.Arithmetic.R2LongAbs.Run(long.MinValue)
using System;
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
[UncheckedArithmetic]
public static class R2LongAbs
{
    public static long Run(long x)
    {
        Spec.Ensures<long>(r => r >= 0, "Not negative.");
        return Math.Abs(x);
    }
}
