// i climbs to n (up to 5), so the invariant i <= 1 is false on the third pass through the body.
// @expect: rejected
// @witness: Corpus.Control.WhileTrue.Run(5)
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class WhileTrue
{
    public static int Run(int n)
    {
        Spec.Requires(n >= 1 && n <= 5, "Small inputs.");
        Spec.Ensures<int>(r => r <= 2, "Stops by two.");
        var i = 0;
        while (true)
        {
            Spec.Decreases(5 - i, "i moves toward five.");
            Spec.LoopInvariant(i <= 1, "i stays small.");
            i = i + 1;
            if (i >= n)
            {
                break;
            }
        }
        return i;
    }
}
