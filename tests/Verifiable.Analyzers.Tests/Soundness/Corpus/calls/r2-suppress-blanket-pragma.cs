// A blanket `#pragma warning disable` (no id) hides the VER102 of a false postcondition; VER107 must still flag it.
// @expect: rejected
// @witness: Corpus.Calls.R2PragmaAll.Next(3)
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public static class R2PragmaAll
{
#pragma warning disable
    public static int Next(int x)
    {
        Spec.Requires(x >= 0 && x <= 1000, "Small.");
        Spec.Ensures<int>(r => r > x, "Claims to grow.");
        return x;
    }
#pragma warning restore
}
