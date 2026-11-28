// A shift count is taken modulo 32, so 1 << 32 is 1, not 0.
// @expect: rejected
// @witness: Corpus.Arithmetic.ShiftMask.Shift(32)
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class ShiftMask
{
    public static int Shift(int n)
    {
        Spec.Requires(n == 32, "The count is 32.");
        Spec.Ensures<int>(r => r == 0, "Shifting by the width shifts everything out.");
        return 1 << n;
    }
}
