// In a for loop continue still runs the update, so the measure drops; c counts the passes that do not continue.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class ForContinue
{
    public static int Run(int n)
    {
        Spec.Requires(n >= 0 && n <= 100, "Small inputs.");
        Spec.Ensures<int>(r => r >= 0 && r <= n, "A count of passes.");
        var c = 0;
        for (var i = 0; i < n; i++)
        {
            Spec.LoopInvariant(i <= n && c >= 0 && c <= i, "Bounded.");
            Spec.Decreases(n - i, "i moves toward n.");
            if (i == 2)
            {
                continue;
            }
            c = c + 1;
        }
        return c;
    }
}
