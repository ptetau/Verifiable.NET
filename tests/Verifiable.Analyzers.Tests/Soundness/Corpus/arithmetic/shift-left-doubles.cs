// A correct program: for small non-negative x, x << 1 is x * 2.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
[CheckedArithmetic]
public static class ShiftDouble
{
    public static int Twice(int x)
    {
        Spec.Requires(x >= 0 && x <= 1000, "Small input.");
        Spec.Ensures<int>(r => r == x * 2, "Shifting left by one doubles.");
        return x << 1;
    }
}
