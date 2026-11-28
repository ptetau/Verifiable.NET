// A correct program: tuple deconstruction swaps two values without a temporary.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Types;

[Verifiable]
public static class TupleSwap
{
    public static int Diff(int a, int b)
    {
        Spec.Requires(a >= 0 && a <= 1000 && b >= 0 && b <= 1000, "Small inputs.");
        Spec.Ensures<int>(r => r == b - a, "Swapping reverses the difference.");
        var x = a;
        var y = b;
        (x, y) = (y, x);
        return x - y;
    }
}
