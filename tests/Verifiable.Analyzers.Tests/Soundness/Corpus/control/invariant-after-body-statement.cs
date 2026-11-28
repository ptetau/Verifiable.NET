// The invariant sits after x = x + 100, where x is far above 3; it only holds at the loop head, not where it is written.
// @expect: rejected
// @witness: Corpus.Control.MidBodyInvariant.Run(0)
// @known-bug: Spec.LoopInvariant is always checked at the loop head, wherever it is written in the body; one placed after a body statement is never checked where it stands.
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class MidBodyInvariant
{
    public static int Run(int x0)
    {
        Spec.Requires(x0 >= 0 && x0 <= 2, "Small inputs.");
        Spec.Ensures<int>(r => r == 3, "Counts up to three.");
        var x = x0;
        while (x < 3)
        {
            x = x + 100;
            Spec.LoopInvariant(x <= 3, "x never passes three.");
            Spec.Decreases(3 - x, "x moves toward three.");
            x = x - 99;
        }
        return x;
    }
}
