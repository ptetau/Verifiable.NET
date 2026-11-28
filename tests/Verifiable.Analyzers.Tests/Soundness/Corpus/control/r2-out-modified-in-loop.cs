// Seven(out t) assigns t inside the loop, so the invariant t == 0 and the result 0 are false for n >= 1.
// @expect: rejected
// @witness: Corpus.Control.OutLoop.Run(1)
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class OutLoop
{
    static void Seven(out int t)
    {
        t = 7;
    }

    public static int Run(int n)
    {
        Spec.Requires(n >= 0 && n <= 10, "Small inputs.");
        Spec.Ensures<int>(r => r == 0, "Untouched.");
        var t = 0;
        var i = 0;
        while (i < n)
        {
            Spec.LoopInvariant(i <= n && t == 0, "t untouched.");
            Spec.Decreases(n - i, "i moves toward n.");
            Seven(out t);
            i = i + 1;
        }
        return t;
    }
}
