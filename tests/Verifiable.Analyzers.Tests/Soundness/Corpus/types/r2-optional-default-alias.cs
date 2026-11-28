// The optional parameter default is default(OId), which violates the alias, and the callee relies on validity.
// @expect: rejected
// @witness: Corpus.Types.OptAlias.Caller()
using Verifiable.Contracts;

namespace Corpus.Types;

[TypeAlias<int>("Ids start at 1.")]
public readonly partial record struct OId
{
    public static bool IsValid(int value) => value >= 1;
}

[Verifiable]
public static class OptAlias
{
    public static int Use(int dummy, OId id = default)
    {
        Spec.Ensures<int>(r => r >= 1, "An id is at least 1.");
        return id.Value;
    }

    public static int Caller()
    {
        Spec.Ensures<int>(r => r >= 1, "Still at least 1.");
        return Use(1);
    }
}
