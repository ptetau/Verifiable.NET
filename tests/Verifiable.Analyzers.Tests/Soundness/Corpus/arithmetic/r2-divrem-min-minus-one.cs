// Math.DivRem(int.MinValue, -1) throws OverflowException like the / operator.
// @expect: rejected
// @witness: Corpus.Arithmetic.R2DivRem.Run(int.MinValue, -1)
using System;
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class R2DivRem
{
    public static int Run(int x, int y)
    {
        Spec.Requires(y != 0, "No division by zero.");
        Spec.Ensures<int>(r => r * y <= x || r * y > x, "Trivially true.");
        var (q, _) = Math.DivRem(x, y);
        return q;
    }
}
