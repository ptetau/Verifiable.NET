// default(Holder) is a struct whose alias field is default (0), which is not a valid HId.
// @expect: rejected
// @witness: Corpus.Types.DefHolder.Caller()
using Verifiable.Contracts;

namespace Corpus.Types;

[TypeAlias<int>("Ids start at 1.")]
public readonly partial record struct HId
{
    public static bool IsValid(int value) => value >= 1;
}

public readonly record struct Holder(HId Id);

[Verifiable]
public static class DefHolder
{
    public static int Raw(HId id)
    {
        Spec.Ensures<int>(r => r >= 1, "An id is at least 1.");
        return id.Value;
    }

    public static int Caller()
    {
        Spec.Ensures<int>(r => r >= 1, "Still at least 1.");
        Holder h = default;
        return Raw(h.Id);
    }
}
