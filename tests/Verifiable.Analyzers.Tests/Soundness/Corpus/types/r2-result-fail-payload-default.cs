// The Value of a failed Result is default(RId), which is not a valid RId, yet it is handed to a callee.
// @expect: rejected
// @witness: Corpus.Types.ResFail.Caller()
using Verifiable.Contracts;

namespace Corpus.Types;

[TypeAlias<int>("Ids start at 1.")]
public readonly partial record struct RId
{
    public static bool IsValid(int value) => value >= 1;
}

[Verifiable]
public static class ResFail
{
    public static int Raw(RId id)
    {
        Spec.Ensures<int>(r => r >= 1, "An id is at least 1.");
        return id.Value;
    }

    public static int Caller()
    {
        Spec.Ensures<int>(r => r >= 1, "Still at least 1.");
        var res = Result<RId, int>.Fail(7);
        return Raw(res.Value);
    }
}
