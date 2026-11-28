// A correct program: under [UncheckedArithmetic], incrementing a byte wraps 255 to 0.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
[UncheckedArithmetic]
public static class UncheckedByte
{
    public static int Next(byte x)
    {
        Spec.Ensures<int>(r => r == (x == 255 ? 0 : x + 1), "Wraps at 255.");
        byte b = x;
        b++;
        return b;
    }

    public static int Narrow(long x)
    {
        Spec.Requires(x >= 0 && x < 1000, "Small input.");
        Spec.Ensures<int>(r => r == x, "Nothing is lost.");
        return (int)x;
    }
}
