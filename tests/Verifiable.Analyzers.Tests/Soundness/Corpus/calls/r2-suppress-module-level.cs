// A module-level [SuppressMessage] (no scope) hides VER102 for the whole module.
// @expect: rejected
// @witness: Corpus.Calls.R2SupMod.Next(3)
using System.Diagnostics.CodeAnalysis;
using Verifiable.Contracts;

[module: SuppressMessage("Verifiable", "VER102")]

namespace Corpus.Calls;

[Verifiable]
public static class R2SupMod
{
    public static int Next(int x)
    {
        Spec.Requires(x >= 0 && x <= 1000, "Small.");
        Spec.Ensures<int>(r => r > x, "Claims to grow.");
        return x;
    }
}
