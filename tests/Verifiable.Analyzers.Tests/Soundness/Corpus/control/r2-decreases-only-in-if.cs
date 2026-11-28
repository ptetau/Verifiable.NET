// The only step toward n is inside an if; with flag false the measure never drops (the loop never ends).
// @expect: rejected
// @witness: Corpus.Control.IfDecrease.Run(2, false)
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class IfDecrease
{
    public static int Run(int n, bool flag)
    {
        Spec.Requires(n >= 0 && n <= 10, "Small inputs.");
        Spec.Ensures<int>(r => r == n, "Counts to n.");
        var i = 0;
        while (i < n)
        {
            Spec.LoopInvariant(i <= n, "In range.");
            Spec.Decreases(n - i, "i moves toward n.");
            if (flag)
            {
                i = i + 1;
            }
        }
        return i;
    }
}
