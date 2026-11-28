// An ImmutableArray built from default(IId) is passed on; the callee reads its first element as a valid IId.
// @expect: rejected
// @witness: Corpus.Types.ArrAlias.Caller()
using System.Collections.Immutable;
using Verifiable.Contracts;

namespace Corpus.Types;

[TypeAlias<int>("Ids start at 1.")]
public readonly partial record struct IId
{
    public static bool IsValid(int value) => value >= 1;
}

[Verifiable]
public static class ArrAlias
{
    public static int First([NonDefault] ImmutableArray<IId> a)
    {
        Spec.Requires(a.Length > 0, "Has an element.");
        Spec.Ensures<int>(r => r >= 1, "An id is at least 1.");
        return a[0].Value;
    }

    public static int Caller()
    {
        Spec.Ensures<int>(r => r >= 1, "Still at least 1.");
        return First(ImmutableArray.Create(default(IId)));
    }
}
