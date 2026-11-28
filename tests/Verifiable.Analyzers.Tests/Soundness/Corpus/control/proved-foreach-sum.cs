// Facts about every item of a list reach the loop body: the last item of a list of small non-negative ints is small
// and non-negative. (A running sum is not used: the loop does not know which item it is on, so it cannot bound a sum.)
// @expect: proved
using System.Collections.Immutable;
using System.Linq;
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class ForeachSum
{
    public static int Sum(ImmutableArray<int> xs)
    {
        Spec.Requires(!xs.IsDefault && xs.Length <= 20 && xs.All(v => v >= 0 && v <= 100), "Small non-negative items.");
        Spec.Ensures<int>(r => r >= 0 && r <= 100, "A valid item, or zero.");
        var last = 0;
        foreach (var v in xs)
        {
            Spec.LoopInvariant(last >= 0 && last <= 100, "Stays a valid item.");
            last = v;
        }
        return last;
    }
}
