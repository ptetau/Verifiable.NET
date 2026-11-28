// The measure x * 2 wraps to a negative number for x >= 2^30, so "Decreases stays non-negative" is false.
// @known-bug: Decreases(x * 2) is accepted although x * 2 wraps negative for x >= 2^30 (measure arithmetic is not checked for int overflow)
// @expect: rejected
// @witness: Corpus.Control.MeasureWraps.Run(1073741824)
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class MeasureWraps
{
    public static int Run(int x)
    {
        Spec.Requires(x >= 0, "Non-negative.");
        Spec.Ensures<int>(r => r == 0, "Counts down to zero.");
        while (x > 0)
        {
            Spec.LoopInvariant(x >= 0, "Non-negative.");
            Spec.Decreases(x * 2, "Twice x goes down.");
            x = x - 1;
        }
        return x;
    }
}
