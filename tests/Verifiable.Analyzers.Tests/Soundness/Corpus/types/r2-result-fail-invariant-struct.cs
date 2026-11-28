// The Value of a failed Result is default(Gap), which violates Gap's invariant (Lo < Hi), yet it reaches Width.
// @expect: rejected
// @witness: Corpus.Types.ResInv.Caller()
using Verifiable.Contracts;

namespace Corpus.Types;

[Verifiable]
public readonly record struct Gap(int Lo, int Hi)
{
    [Invariant("Lo is below Hi.")]
    public bool Ordered() => Lo < Hi;
}

[Verifiable]
public static class ResInv
{
    public static int Width(Gap g)
    {
        Spec.Ensures<int>(r => r >= 1, "Positive width.");
        return g.Hi - g.Lo;
    }

    public static int Caller()
    {
        Spec.Ensures<int>(r => r >= 1, "Positive width.");
        var res = Result<Gap, int>.Fail(1);
        return Width(res.Value);
    }
}
