// Bump(ref t) changes t inside the loop, so the invariant t == 0 and the result 0 are false for n >= 1.
// @expect: rejected
// @witness: Corpus.Control.RefLoop.Run(1)
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class RefLoop
{
    static void Bump(ref int t)
    {
        t = t + 1;
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
            Bump(ref t);
            i = i + 1;
        }
        return t;
    }
}
