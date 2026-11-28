// The inner loop moves the outer counter i back by 2, so the outer measure n - i grows on every pass.
// @expect: rejected
// @witness: Corpus.Control.SharedCounter.Run(1)
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class SharedCounter
{
    public static int Run(int n)
    {
        Spec.Requires(n >= 1 && n <= 10, "Small inputs.");
        Spec.Ensures<int>(r => r == n, "Counts to n.");
        var i = 0;
        while (i < n)
        {
            Spec.LoopInvariant(i <= n, "Not past n.");
            Spec.Decreases(n - i, "i moves toward n.");
            var j = 0;
            while (j < 2)
            {
                Spec.LoopInvariant(j <= 2, "In range.");
                Spec.Decreases(2 - j, "j moves toward 2.");
                j = j + 1;
                i = i - 1;
            }
            i = i + 1;
        }
        return i;
    }
}
