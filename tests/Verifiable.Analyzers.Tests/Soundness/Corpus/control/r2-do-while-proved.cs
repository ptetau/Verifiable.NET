// A do-while that runs the body at least once; the measure n - i is at least 1 on every pass.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class DoWhileCount
{
    public static int Run(int n)
    {
        Spec.Requires(n >= 1 && n <= 100, "Small positive inputs.");
        Spec.Ensures<int>(r => r == n, "Counts to n.");
        var i = 0;
        do
        {
            Spec.LoopInvariant(i >= 0 && i < n, "Below n at the top of each pass.");
            Spec.Decreases(n - i, "i moves toward n.");
            i = i + 1;
        }
        while (i < n);
        return i;
    }
}
