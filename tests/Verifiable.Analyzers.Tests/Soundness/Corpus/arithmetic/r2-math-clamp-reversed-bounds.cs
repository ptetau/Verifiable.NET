// Math.Clamp throws ArgumentException when min > max.
// @expect: rejected
// @witness: Corpus.Arithmetic.R2ClampThrows.Run(0, 5, 0)
using System;
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class R2ClampThrows
{
    public static int Run(int x, int lo, int hi)
    {
        Spec.Ensures<int>(r => r >= lo, "At least lo.");
        return Math.Clamp(x, lo, hi);
    }
}
