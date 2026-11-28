// [NonDefault] on the outer array says nothing about the inner arrays; an inner default array is then read.
// @expect: rejected
// @witness: Corpus.Types.NestedArr.Inner(System.Collections.Immutable.ImmutableArray.Create(default(System.Collections.Immutable.ImmutableArray<int>)))
using System.Collections.Immutable;
using Verifiable.Contracts;

namespace Corpus.Types;

[Verifiable]
public static class NestedArr
{
    public static int Inner([NonDefault] ImmutableArray<ImmutableArray<int>> a)
    {
        Spec.Requires(a.Length > 0, "Has a first row.");
        Spec.Ensures<int>(r => r >= 0, "A length is never negative.");
        return a[0].Length;
    }
}
