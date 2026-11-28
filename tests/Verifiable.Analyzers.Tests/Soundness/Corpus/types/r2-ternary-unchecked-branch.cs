// x >= 0 admits 0, so the unchecked TId in the true branch can be invalid when it reaches the callee.
// @expect: rejected
// @witness: Corpus.Types.TernAlias.Caller(0)
using Verifiable.Contracts;

namespace Corpus.Types;

[TypeAlias<int>("Ids start at 1.")]
public readonly partial record struct TId
{
    public static bool IsValid(int value) => value >= 1;
}

[Verifiable]
public static class TernAlias
{
    public static int Raw(TId id)
    {
        Spec.Ensures<int>(r => r >= 1, "An id is at least 1.");
        return id.Value;
    }

    public static int Caller(int x)
    {
        Spec.Ensures<int>(r => r >= 1, "Still at least 1.");
        return Raw(x >= 0 ? new TId(x) : TId.From(1));
    }
}
