// The helper's parameter x shadows the caller's x, so Twice(x + 1) is 2 * (x + 1). This is the form to use instead of a
// local function.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class StaticHelperShadowing
{
    public static int Twice(int x)
    {
        Spec.Requires(x >= -1000 && x <= 1000, "Small inputs.");
        Spec.Ensures<int>(r => r == 2 * x, "Doubles x.");
        return x * 2;
    }

    public static int Run(int x)
    {
        Spec.Requires(x >= -1000 && x <= 999, "Small inputs, so that x + 1 is still small.");
        Spec.Ensures<int>(r => r == 2 * x + 2, "Doubles x + 1.");
        return Twice(x + 1);
    }
}
