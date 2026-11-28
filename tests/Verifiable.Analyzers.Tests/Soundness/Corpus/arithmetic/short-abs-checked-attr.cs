// A correct program: away from short.MinValue, Math.Abs of a short is not negative.
// @expect: proved
using System;
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
[CheckedArithmetic]
public static class ShortAbs
{
    public static short Magnitude(short x)
    {
        Spec.Requires(x > short.MinValue, "Not the minimum value.");
        Spec.Ensures<short>(r => r >= 0, "Magnitude is not negative.");
        return Math.Abs(x);
    }
}
