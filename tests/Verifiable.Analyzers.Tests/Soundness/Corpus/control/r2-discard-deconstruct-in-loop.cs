// (_, c) = (1, c + 1) assigns c although the first slot is discarded.
// @expect: rejected
// @witness: Corpus.Control.DiscardLoop.Run(1)
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class DiscardLoop
{
    public static int Run(int n)
    {
        Spec.Requires(n >= 0 && n <= 10, "Small inputs.");
        Spec.Ensures<int>(r => r == 0, "Untouched.");
        var c = 0;
        var i = 0;
        while (i < n)
        {
            Spec.LoopInvariant(i <= n && c == 0, "c untouched.");
            Spec.Decreases(n - i, "i moves toward n.");
            (_, c) = (1, c + 1);
            i = i + 1;
        }
        return c;
    }
}
