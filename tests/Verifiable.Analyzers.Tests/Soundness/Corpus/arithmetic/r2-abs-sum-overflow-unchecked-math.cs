// Math.Abs(x) + Math.Abs(y) can exceed int.MaxValue; in an [UncheckedArithmetic] method the sum wraps, so it is not always at least x's magnitude.
// @expect: rejected
// @witness: Corpus.Arithmetic.R2AbsSum.Run(2147483647, 2147483647)
using System;
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
[UncheckedArithmetic]
public static class R2AbsSum
{
    public static int Run(int x, int y)
    {
        Spec.Requires(x != int.MinValue && y != int.MinValue, "Abs is defined.");
        Spec.Ensures<int>(r => r >= 0, "Not negative.");
        return Math.Abs(x) + Math.Abs(y);
    }
}
