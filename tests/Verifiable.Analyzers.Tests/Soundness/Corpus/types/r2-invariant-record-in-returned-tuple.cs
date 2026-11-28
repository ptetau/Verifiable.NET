// A tuple returned from a helper hides an invariant-violating record; Width relies on the invariant.
// @expect: rejected
// @witness: Corpus.Types.TupInv.Caller(5)
using Verifiable.Contracts;

namespace Corpus.Types;

[Verifiable]
public sealed record Seg(int Lo, int Hi)
{
    [Invariant("Lo never exceeds Hi.")]
    public bool Ordered() => Lo <= Hi;
}

[Verifiable]
public static class TupInv
{
    public static (Seg, int) Make(int a)
    {
        Spec.Requires(a >= 0 && a <= 1000, "Small.");
        return (new Seg(a, a - 1), 1);
    }

    public static int Width(Seg s)
    {
        Spec.Ensures<int>(r => r >= 0, "Non-negative.");
        return s.Hi - s.Lo;
    }

    public static int Caller(int a)
    {
        Spec.Requires(a >= 0 && a <= 1000, "Small.");
        Spec.Ensures<int>(r => r >= 0, "Non-negative.");
        return Width(Make(a).Item1);
    }
}
