// Nested foreach over the same list: both items satisfy the list's item facts.
// @expect: proved
using System.Collections.Immutable;
using System.Linq;
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class ForeachNested
{
    public static int Run(ImmutableArray<int> xs)
    {
        Spec.Requires(!xs.IsDefault && xs.Length <= 20 && xs.All(v => v >= 0 && v <= 100), "Small non-negative items.");
        Spec.Ensures<int>(r => r >= 0 && r <= 200, "A sum of two items, or zero.");
        var last = 0;
        foreach (var a in xs)
        {
            Spec.LoopInvariant(last >= 0 && last <= 200, "Bounded.");
            foreach (var b in xs)
            {
                Spec.LoopInvariant(last >= 0 && last <= 200, "Bounded.");
                last = a + b;
            }
        }
        return last;
    }
}
