// The switch expression has no arm for 0, so Sign(0) throws SwitchExpressionException instead of returning.
// @expect: rejected
// @witness: Corpus.Control.MissingArm.Sign(0)
// @known-bug: The implicit throw of a non-exhaustive switch expression is a path with no obligation, so the missing arm is never reported.
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class MissingArm
{
    public static int Sign(int x)
    {
        Spec.Ensures<int>(r => r == 1 || r == -1, "A unit sign.");
#pragma warning disable CS8509
        return x switch
        {
            > 0 => 1,
            < 0 => -1,
        };
#pragma warning restore CS8509
    }
}
