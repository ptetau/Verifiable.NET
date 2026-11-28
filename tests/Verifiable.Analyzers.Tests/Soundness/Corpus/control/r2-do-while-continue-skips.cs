// In a do-while, continue jumps to the condition and skips the step: the second pass has the same measure.
// @expect: rejected
// @witness: Corpus.Control.DoContinue.Run(2)
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class DoContinue
{
    public static int Run(int n)
    {
        Spec.Requires(n >= 1 && n <= 10, "Small inputs.");
        Spec.Ensures<int>(r => r == n, "Counts to n.");
        var i = 0;
        var s = 0;
        do
        {
            Spec.LoopInvariant(i <= n && s >= 0 && s <= 1, "In range.");
            Spec.Decreases(n - i, "i moves toward n.");
            if (s == 0)
            {
                s = 1;
                continue;
            }
            i = i + 1;
        }
        while (i < n);
        return i;
    }
}
