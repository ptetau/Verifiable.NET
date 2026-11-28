// An unchecked alias travels inside a value tuple and is read back out as Item1.
// @expect: rejected
// @witness: Corpus.Types.TupAlias.Caller(0)
using Verifiable.Contracts;

namespace Corpus.Types;

[TypeAlias<int>("Ids start at 1.")]
public readonly partial record struct VId
{
    public static bool IsValid(int value) => value >= 1;
}

[Verifiable]
public static class TupAlias
{
    public static int Raw(VId id)
    {
        Spec.Ensures<int>(r => r >= 1, "An id is at least 1.");
        return id.Value;
    }

    public static int Caller(int x)
    {
        Spec.Ensures<int>(r => r >= 1, "Still at least 1.");
        var t = (new VId(x), 1);
        return Raw(t.Item1);
    }
}
