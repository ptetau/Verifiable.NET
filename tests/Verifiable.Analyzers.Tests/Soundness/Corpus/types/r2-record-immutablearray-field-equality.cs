// Record equality compares the ImmutableArray field by its underlying reference, not by contents.
// @expect: rejected
// @witness: Corpus.Types.BagEq.Same(1)
using System.Collections.Immutable;
using Verifiable.Contracts;

namespace Corpus.Types;

public sealed record Bag([property: NonDefault] ImmutableArray<int> Items);

[Verifiable]
public static class BagEq
{
    public static bool Same(int x)
    {
        Spec.Ensures<bool>(r => r == true, "Same contents mean equal bags.");
        return new Bag(ImmutableArray.Create(x)) == new Bag(ImmutableArray.Create(x));
    }
}
