// The guards cover > 0 and < 0 only; Sign(0) matches no arm and throws.
// @expect: rejected
// @witness: Corpus.Control.GuardGap.Sign(0)
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class GuardGap
{
    public static int Sign(int x)
    {
        Spec.Ensures<int>(r => r == 1 || r == -1, "A unit sign.");
#pragma warning disable CS8509
        return x switch
        {
            var v when v > 0 => 1,
            var v when v < 0 => -1,
        };
#pragma warning restore CS8509
    }
}
