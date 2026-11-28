// Result.Ok wraps an unchecked alias, and Value is then handed to a callee.
// @expect: rejected
// @witness: Corpus.Types.ResOk.Caller(0)
using Verifiable.Contracts;

namespace Corpus.Types;

[TypeAlias<int>("Ids start at 1.")]
public readonly partial record struct OkId
{
    public static bool IsValid(int value) => value >= 1;
}

[Verifiable]
public static class ResOk
{
    public static int Raw(OkId id)
    {
        Spec.Ensures<int>(r => r >= 1, "An id is at least 1.");
        return id.Value;
    }

    public static int Caller(int x)
    {
        Spec.Ensures<int>(r => r >= 1, "Still at least 1.");
        var res = Result<OkId, int>.Ok(new OkId(x));
        return Raw(res.Value);
    }
}
