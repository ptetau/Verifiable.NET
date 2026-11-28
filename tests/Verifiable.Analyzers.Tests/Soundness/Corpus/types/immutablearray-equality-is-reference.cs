// ImmutableArray == compares the underlying array references, so two separately built arrays are never ==.
// @expect: rejected
// @witness: Corpus.Types.ArrayEquality.Same(1)
// @known-bug: ImmutableArray == is lowered as structural sequence equality, but at run time it compares the underlying array references.
using System.Collections.Immutable;
using Verifiable.Contracts;

namespace Corpus.Types;

[Verifiable]
public static class ArrayEquality
{
    public static bool Same(int x)
    {
        Spec.Ensures<bool>(r => r == true, "Arrays with equal items are equal.");
        return ImmutableArray.Create(x) == ImmutableArray.Create(x);
    }
}
