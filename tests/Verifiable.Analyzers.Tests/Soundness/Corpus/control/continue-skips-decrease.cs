// On the continue path the measure n - i does not go down, so the Decreases claim is false (the loop still ends).
// @expect: rejected
// @witness: Corpus.Control.ContinueDecreases.Run(2)
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class ContinueDecreases
{
    public static int Run(int n)
    {
        Spec.Requires(n >= 0 && n <= 10, "Small inputs.");
        Spec.Ensures<int>(r => r == n, "Counts to n.");
        var i = 0;
        var skipped = 0;
        while (i < n)
        {
            Spec.Decreases(n - i, "i moves toward n.");
            Spec.LoopInvariant(i <= n && skipped >= 0 && skipped <= 1, "In range.");
            if (skipped == 0)
            {
                skipped = 1;
                continue;
            }
            i = i + 1;
        }
        return i;
    }
}
