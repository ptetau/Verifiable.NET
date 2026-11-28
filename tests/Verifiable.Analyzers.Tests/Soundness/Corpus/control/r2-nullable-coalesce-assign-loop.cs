// v ??= 5 assigns v when it is null; the invariant v == null and the result 0 are false for n >= 1.
// @expect: rejected
// @witness: Corpus.Control.CoalesceLoop.Run(1)
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class CoalesceLoop
{
    public static int Run(int n)
    {
        Spec.Requires(n >= 0 && n <= 10, "Small inputs.");
        Spec.Ensures<int>(r => r == 0, "Untouched.");
        int? v = null;
        var i = 0;
        while (i < n)
        {
            Spec.LoopInvariant(i <= n && v == null, "v stays null.");
            Spec.Decreases(n - i, "i moves toward n.");
            v ??= 5;
            i = i + 1;
        }
        return v ?? 0;
    }
}
