// Summing a list of non-negative ints gives a non-negative total.
// @expect: proved
// @known-bug: False rejection: the foreach item is an unconstrained value (the enumerator is not modelled), so facts about the items from the precondition never reach the loop body.
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
        Spec.Ensures<int>(r => r >= 0, "Non-negative total.");
        var total = 0;
        foreach (var v in xs)
        {
            Spec.LoopInvariant(total >= 0, "Running total stays non-negative.");
            total = total + v;
        }
        return total;
    }
}
