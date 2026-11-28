// The loop in the local function Count moves i, so the invariant i == 0 is false on the second pass.
// @expect: rejected
// @witness: Corpus.Control.LocalLoop.Run(2)
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class LocalLoop
{
    public static int Run(int x)
    {
        Spec.Requires(x >= 0 && x <= 10, "Small inputs.");
        Spec.Ensures<int>(r => r >= 0, "Non-negative.");
        int Count(int y)
        {
            var i = 0;
            while (i < y)
            {
                Spec.Decreases(y - i, "i moves toward y.");
                Spec.LoopInvariant(i == 0, "i never moves.");
                i = i + 1;
            }
            return i;
        }
        Count(x);
        return x;
    }
}
