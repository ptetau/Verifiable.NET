// (x -= 3) > 0 runs before every pass; from x == 0 the loop exits at once with x == -3, below the claimed bound.
// @expect: rejected
// @witness: Corpus.Control.CondSubtractWrong.Run(0)
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class CondSubtractWrong
{
    public static int Run(int x)
    {
        Spec.Requires(x >= 0 && x <= 100, "Small inputs.");
        Spec.Ensures<int>(r => r <= 0 && r >= -2, "Just below zero.");
        while ((x -= 3) > 0)
        {
            Spec.LoopInvariant(x >= 0 && x <= 100, "In range.");
            Spec.Decreases(x, "x goes down.");
        }
        return x;
    }
}
