// With no upper bound on n the running sum overflows and goes negative.
// @expect: rejected
// @witness: Corpus.Control.SumOverflow.Run(100000)
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class SumOverflow
{
    public static int Run(int n)
    {
        Spec.Requires(n >= 0, "Non-negative.");
        Spec.Ensures<int>(r => r >= 0, "A sum of non-negatives.");
        var s = 0;
        var i = 0;
        while (i < n)
        {
            Spec.LoopInvariant(i <= n && s >= 0, "Non-negative.");
            Spec.Decreases(n - i, "i moves toward n.");
            s = s + i;
            i = i + 1;
        }
        return s;
    }
}
