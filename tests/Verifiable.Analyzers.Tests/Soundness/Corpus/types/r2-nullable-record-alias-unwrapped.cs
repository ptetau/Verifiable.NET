// An alias constructed unchecked is stored in a record's nullable field, unwrapped, and passed to a callee.
// @expect: rejected
// @witness: Corpus.Types.NullBox.Caller(0)
using Verifiable.Contracts;

namespace Corpus.Types;

[TypeAlias<int>("Ids start at 1.")]
public readonly partial record struct NId
{
    public static bool IsValid(int value) => value >= 1;
}

public sealed record NBox(NId? Id);

[Verifiable]
public static class NullBox
{
    public static int Raw(NId id)
    {
        Spec.Ensures<int>(r => r >= 1, "An id is at least 1.");
        return id.Value;
    }

    public static int Caller(int x)
    {
        Spec.Ensures<int>(r => r >= 1, "Still at least 1.");
        var b = new NBox(new NId(x));
        return Raw(b.Id!.Value);
    }
}
