// The loop never exits for any input, so the method never returns; the postcondition after it is only vacuously true.
// @expect: rejected
// @witness: Corpus.Control.Spin.Run(0)
// @runtime: skip Every run loops forever.
// @known-bug: Termination is not checked: a while loop with only Spec.LoopInvariant (no Decreases) is accepted, and code after a loop that never exits is proved vacuously.
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class Spin
{
    public static int Run(int x)
    {
        Spec.Requires(x >= 0, "Non-negative input.");
        Spec.Ensures<int>(r => r == -1, "Returns minus one.");
        var i = x;
        while (i >= 0)
        {
            Spec.LoopInvariant(i >= 0, "i stays non-negative.");
            i = i + 1 - 1;
        }
        return i;
    }
}
