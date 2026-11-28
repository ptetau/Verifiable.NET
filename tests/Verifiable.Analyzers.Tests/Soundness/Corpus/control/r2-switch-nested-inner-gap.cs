// The inner switch has no arm for y == 0, and the outer arm for x > 0 reaches it.
// @expect: rejected
// @witness: Corpus.Control.NestedGap.Run(1, 0)
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class NestedGap
{
    public static int Run(int x, int y)
    {
        Spec.Ensures<int>(r => r >= 0 && r <= 2, "A small class.");
#pragma warning disable CS8509
        return x switch
        {
            > 0 => y switch
            {
                > 0 => 1,
                < 0 => 2,
            },
            _ => 0,
        };
#pragma warning restore CS8509
    }
}
