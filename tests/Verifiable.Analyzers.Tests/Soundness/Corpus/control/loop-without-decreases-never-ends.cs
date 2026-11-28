// The loop never exits for any input, so the method never returns; the postcondition after it is only vacuously true.
// @expect: rejected
// @witness: Corpus.Control.Spin.Run(0)
// @runtime: skip Every run loops forever.
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class Spin
{
    public static int Run(int x)
    {
        Spec.Requires(x >= 0 && x <= 1000, "Small non-negative input.");
        Spec.Ensures<int>(r => r == -1, "Returns minus one.");
        var i = x;
        while (i >= 0)
        {
            Spec.LoopInvariant(i >= 0 && i <= 1000, "i stays small and non-negative.");
            i = i + 1 - 1;
        }
        return i;
    }
}
