// A params ImmutableArray carries an unchecked alias in its last slot; the callee returns that slot.
// @expect: rejected
// @witness: Corpus.Types.ParamsAlias.Caller(0)
using System.Collections.Immutable;
using Verifiable.Contracts;

namespace Corpus.Types;

[TypeAlias<int>("Ids start at 1.")]
public readonly partial record struct PId
{
    public static bool IsValid(int value) => value >= 1;
}

[Verifiable]
public static class ParamsAlias
{
    public static int Last([NonDefault] params ImmutableArray<PId> ids)
    {
        Spec.Requires(ids.Length > 0, "At least one id.");
        Spec.Ensures<int>(r => r >= 1, "An id is at least 1.");
        return ids[ids.Length - 1].Value;
    }

    public static int Caller(int x)
    {
        Spec.Ensures<int>(r => r >= 1, "Still at least 1.");
        return Last(PId.From(1), new PId(x));
    }
}
