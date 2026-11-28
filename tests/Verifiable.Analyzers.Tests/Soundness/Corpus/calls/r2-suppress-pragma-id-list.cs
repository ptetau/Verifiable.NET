// The pragma lists VER102 among several ids, in a different order and with a nullable id first.
// @expect: rejected
// @witness: Corpus.Calls.R2PragmaList.Next(3)
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public static class R2PragmaList
{
#pragma warning disable CS8600, VER105, VER102
    public static int Next(int x)
    {
        Spec.Requires(x >= 0 && x <= 1000, "Small.");
        Spec.Ensures<int>(r => r > x, "Claims to grow.");
        return x;
    }
#pragma warning restore CS8600, VER105, VER102
}
