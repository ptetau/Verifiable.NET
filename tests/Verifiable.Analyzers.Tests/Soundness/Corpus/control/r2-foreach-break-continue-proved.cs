// break and continue in foreach: an item is never negative, so the break is never taken.
// @expect: proved
using System.Collections.Immutable;
using System.Linq;
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class ForeachBreak
{
    public static int Run(ImmutableArray<int> xs)
    {
        Spec.Requires(!xs.IsDefault && xs.Length <= 20 && xs.All(v => v >= 0 && v <= 100), "Small non-negative items.");
        Spec.Ensures<int>(r => r >= 0 && r <= 100, "A valid item, or zero.");
        var neg = false;
        var pos = 0;
        foreach (var v in xs)
        {
            Spec.LoopInvariant(!neg && pos >= 0 && pos <= 100, "Valid.");
            if (v == 0)
            {
                continue;
            }
            if (v < 0)
            {
                neg = true;
                break;
            }
            pos = v;
        }
        return neg ? -1 : pos;
    }
}
