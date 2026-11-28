// An out parameter is assigned an unchecked alias; the caller passes it on as valid.
// @expect: rejected
// @witness: Corpus.Types.OutAlias.Caller(0)
using Verifiable.Contracts;

namespace Corpus.Types;

[TypeAlias<int>("Ids start at 1.")]
public readonly partial record struct UId
{
    public static bool IsValid(int value) => value >= 1;
}

[Verifiable]
public static class OutAlias
{
    public static void Make(int x, out UId id)
    {
        id = new UId(x);
    }

    public static int Raw(UId id)
    {
        Spec.Ensures<int>(r => r >= 1, "An id is at least 1.");
        return id.Value;
    }

    public static int Caller(int x)
    {
        Spec.Ensures<int>(r => r >= 1, "Still at least 1.");
        Make(x, out var id);
        return Raw(id);
    }
}
