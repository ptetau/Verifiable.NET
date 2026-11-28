// Same loop with the right bound: after the last (x -= 3) the value is between -3 and 0.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class CondSubtract
{
    public static int Run(int x)
    {
        Spec.Requires(x >= 0 && x <= 100, "Small inputs.");
        Spec.Ensures<int>(r => r <= 0 && r >= -3, "Just below zero.");
        while ((x -= 3) > 0)
        {
            Spec.LoopInvariant(x >= 0 && x <= 100, "In range.");
            Spec.Decreases(x, "x goes down.");
        }
        return x;
    }
}
