// Stepping by 2 from an odd y skips 0: y becomes -1 and the measure y is negative (the loop never ends).
// @expect: rejected
// @witness: Corpus.Control.NegativeMeasure.Run(1)
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class NegativeMeasure
{
    public static int Run(int x)
    {
        Spec.Requires(x >= 0 && x <= 10, "Small inputs.");
        Spec.Ensures<int>(r => r == 0, "Counts down to zero.");
        var y = x;
        while (y != 0)
        {
            Spec.LoopInvariant(y <= 10, "Not above ten.");
            Spec.Decreases(y, "y goes down.");
            y = y - 2;
        }
        return y;
    }
}
