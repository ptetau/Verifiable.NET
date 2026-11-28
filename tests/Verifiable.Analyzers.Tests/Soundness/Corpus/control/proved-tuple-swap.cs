// A tuple swap is simultaneous: both right-hand values are read before either variable is written.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class TupleSwap
{
    public static int Run(int x, int y)
    {
        Spec.Requires(x >= -1000 && x <= 1000 && y >= -1000 && y <= 1000, "Small inputs.");
        Spec.Ensures<int>(r => r == 10 * y + x, "a and b trade places.");
        var a = x;
        var b = y;
        (a, b) = (b, a);
        return 10 * a + b;
    }
}
