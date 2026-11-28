// A loop that only exits through return: the measure n - i reaches 0 exactly when the return is taken.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class ReturnOnlyExit
{
    public static int Run(int n)
    {
        Spec.Requires(n >= 0 && n <= 100, "Small inputs.");
        Spec.Ensures<int>(r => r == n, "Counts to n.");
        var i = 0;
        while (true)
        {
            Spec.LoopInvariant(i >= 0 && i <= n, "In range.");
            Spec.Decreases(n - i, "i moves toward n.");
            if (i == n)
            {
                return i;
            }
            i = i + 1;
        }
    }
}
