// A correct program: away from short.MinValue, Math.Abs of a short is not negative.
// @expect: proved
// @known-bug: False rejection (VER103): under [CheckedArithmetic], Math.Abs on a short compares an Int with a 32-bit bit-vector zero, a Z3 sort error.
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
