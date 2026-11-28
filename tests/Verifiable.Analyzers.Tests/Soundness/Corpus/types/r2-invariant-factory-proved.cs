// A correct program: a static factory guards the invariant with a precondition.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Types;

[Verifiable]
public sealed record Rng(int Lo, int Hi)
{
    [Invariant("Lo never exceeds Hi.")]
    public bool Ordered() => Lo <= Hi;

    public static Rng Make(int lo, int hi)
    {
        Spec.Requires(lo <= hi, "Ordered bounds.");
        Spec.Ensures<Rng>(r => r.Lo <= r.Hi, "Ordered.");
        return new Rng(lo, hi);
    }
}

[Verifiable]
public static class RngUse
{
    public static int Width(int lo, int hi)
    {
        Spec.Requires(lo >= 0 && lo <= hi && hi <= 1000, "Small ordered.");
        Spec.Ensures<int>(r => r >= 0, "Non-negative.");
        var g = Rng.Make(lo, hi);
        return g.Hi - g.Lo;
    }
}
