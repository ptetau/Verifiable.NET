// A #pragma hides the VER102 of a false postcondition. VER107 reports the pragma, and it is an error by default.
// @expect: rejected
// @witness: Corpus.Calls.PragmaHidden.Next(3)
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public static class PragmaHidden
{
#pragma warning disable VER102
    public static int Next(int x)
    {
        Spec.Requires(x >= 0 && x <= 1000, "Small inputs only.");
        Spec.Ensures<int>(r => r > x, "Claims to grow, but returns its input.");
        return x;
    }
#pragma warning restore VER102
}
