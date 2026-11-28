// The optional parameter default is default(Gap2), which breaks its invariant, and the callee relies on it.
// @expect: rejected
// @witness: Corpus.Types.OptInv.Caller()
using Verifiable.Contracts;

namespace Corpus.Types;

[Verifiable]
public readonly record struct Gap2(int Lo, int Hi)
{
    [Invariant("Lo is below Hi.")]
    public bool Ordered() => Lo < Hi;
}

[Verifiable]
public static class OptInv
{
    public static int Width(int pad, Gap2 g = default)
    {
        Spec.Ensures<int>(r => r >= 1, "Positive width.");
        return g.Hi - g.Lo;
    }

    public static int Caller()
    {
        Spec.Ensures<int>(r => r >= 1, "Positive width.");
        return Width(0);
    }
}
