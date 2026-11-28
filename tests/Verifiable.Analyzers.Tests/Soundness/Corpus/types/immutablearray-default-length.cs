// default(ImmutableArray<int>).Length throws NullReferenceException; it is not 0.
// @expect: rejected
// @witness: Corpus.Types.DefaultArrayLength.Len(default(System.Collections.Immutable.ImmutableArray<int>))
using System.Collections.Immutable;
using Verifiable.Contracts;

namespace Corpus.Types;

[Verifiable]
public static class DefaultArrayLength
{
    public static int Len(ImmutableArray<int> items)
    {
        Spec.Ensures<int>(r => r >= 0, "A length is never negative.");
        return items.Length;
    }
}
