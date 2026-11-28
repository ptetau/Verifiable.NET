// Math.Abs(int.MinValue) throws OverflowException, so the absolute value is not always returned.
// @expect: rejected
// @witness: Corpus.Arithmetic.AbsMin.Size(int.MinValue)
using System;
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class AbsMin
{
    public static int Size(int x)
    {
        Spec.Ensures<int>(r => r >= 0, "A size is never negative.");
        return Math.Abs(x);
    }
}
