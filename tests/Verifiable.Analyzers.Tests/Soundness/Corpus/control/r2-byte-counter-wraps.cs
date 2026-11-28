// A byte counter wraps from 255 to 0, so for n in 256..300 the loop never ends.
// @expect: rejected
// @witness: Corpus.Control.ByteCounter.Run(300)
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class ByteCounter
{
    public static int Run(int n)
    {
        Spec.Requires(n >= 0 && n <= 300, "Small inputs.");
        Spec.Ensures<int>(r => r == n, "Counts to n.");
        byte i = 0;
        while (i < n)
        {
            Spec.LoopInvariant(i <= n, "In range.");
            Spec.Decreases(n - i, "i moves toward n.");
            i++;
        }
        return i;
    }
}
