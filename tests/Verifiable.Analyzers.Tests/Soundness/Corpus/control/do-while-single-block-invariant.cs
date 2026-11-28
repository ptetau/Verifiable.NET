// i reaches n (up to 5), so the invariant i <= 2 fails on the third pass through the body.
// @expect: rejected
// @witness: Corpus.Control.DoWhile.Run(5)
// @known-bug: When a do-while body is one basic block, its back edge targets the header itself and is skipped, so LoopInvariant and Decreases are never checked after an iteration.
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class DoWhile
{
    public static int Run(int n)
    {
        Spec.Requires(n >= 1 && n <= 5, "Small inputs.");
        Spec.Ensures<int>(r => r <= 3, "Stops by three.");
        var i = 0;
        do
        {
            Spec.Decreases(5 - i, "i moves toward five.");
            Spec.LoopInvariant(i <= 2, "i stays small.");
            i = i + 1;
        }
        while (i < n);
        return i;
    }
}
