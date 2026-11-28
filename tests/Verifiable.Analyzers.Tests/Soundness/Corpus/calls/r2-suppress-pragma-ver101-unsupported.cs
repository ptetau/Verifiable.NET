// A pragma hides VER101 on a contract the analyzer can't translate (a call into an unmodelled library); the proof is silently dropped.
// @expect: rejected
// @witness: Corpus.Calls.R2PragmaUnsup.Next(3)
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public static class R2PragmaUnsup
{
#pragma warning disable VER101, VER006
    public static int Next(int x)
    {
        Spec.Requires(x >= 0 && x <= 1000, "Small.");
        Spec.Ensures<int>(r => r > x && System.Math.Sign(r) == 1, "Claims to grow.");
        return x;
    }
#pragma warning restore VER101, VER006
}
