// default(GId) passes through a generic identity method and reaches a callee that assumes GId is valid.
// @expect: rejected
// @witness: Corpus.Types.GenPass.Caller()
using Verifiable.Contracts;

namespace Corpus.Types;

[TypeAlias<int>("Ids start at 1.")]
public readonly partial record struct GId
{
    public static bool IsValid(int value) => value >= 1;
}

[Verifiable]
public static class GenPass
{
    public static T Pass<T>(T x) => x;

    public static int Raw(GId id)
    {
        Spec.Ensures<int>(r => r >= 1, "An id is at least 1.");
        return id.Value;
    }

    public static int Caller()
    {
        Spec.Ensures<int>(r => r >= 1, "Still at least 1.");
        return Raw(Pass(default(GId)));
    }
}
