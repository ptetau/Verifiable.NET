// A correct program: the analyzer must prove it, and running it must keep its contract.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Controls;

[Verifiable]
public static class BoundedAdd
{
    public static int AddFive(int x)
    {
        Spec.Requires(x >= 0 && x <= 1000, "Small inputs only.");
        Spec.Ensures<int>(r => r >= 5 && r == x + 5, "Adds five.");
        return x + 5;
    }
}
