// An unchecked alias is stored in a record, deconstructed into a local, and passed on.
// @expect: rejected
// @witness: Corpus.Types.DecAlias.Caller(0)
using Verifiable.Contracts;

namespace Corpus.Types;

[TypeAlias<int>("Ids start at 1.")]
public readonly partial record struct DId
{
    public static bool IsValid(int value) => value >= 1;
}

public sealed record DItem(DId Id, int N);

[Verifiable]
public static class DecAlias
{
    public static int Raw(DId id)
    {
        Spec.Ensures<int>(r => r >= 1, "An id is at least 1.");
        return id.Value;
    }

    public static int Caller(int x)
    {
        Spec.Ensures<int>(r => r >= 1, "Still at least 1.");
        var (id, n) = new DItem(new DId(x), 1);
        return Raw(id);
    }
}
