// A #pragma hides the VER102 of a false postcondition. VER107 reports the pragma, but only as a warning.
// @expect: rejected
// @witness: Corpus.Calls.PragmaHidden.Next(3)
// @known-bug: VER107 has default severity Warning (documented), so a #pragma disabling VER102 lets a false proof build; the corpus does not count a warning as a rejection.
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
