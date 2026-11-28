// The loop walks xs.Select(a => -a), not xs: its items are negative although every item of xs is non-negative.
// @expect: rejected
// @witness: Corpus.Control.ForeachSelect.Run(System.Collections.Immutable.ImmutableArray.Create(5))
using System.Collections.Immutable;
using System.Linq;
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class ForeachSelect
{
    public static int Run(ImmutableArray<int> xs)
    {
        Spec.Requires(!xs.IsDefault && xs.Length <= 20 && xs.All(v => v >= 0 && v <= 100), "Small non-negative items.");
        Spec.Ensures<int>(r => r >= 0, "Non-negative.");
        var last = 0;
        foreach (var v in xs.Select(a => -a))
        {
            Spec.LoopInvariant(last >= -100 && last <= 100, "Bounded.");
            last = v;
        }
        return last;
    }
}
