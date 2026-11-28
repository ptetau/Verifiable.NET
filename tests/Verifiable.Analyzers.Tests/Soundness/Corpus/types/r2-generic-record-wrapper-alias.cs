// A generic record instantiated with an alias carries an unchecked value to a callee.
// @expect: rejected
// @witness: Corpus.Types.GenWrap.Caller(0)
using Verifiable.Contracts;

namespace Corpus.Types;

[TypeAlias<int>("Ids start at 1.")]
public readonly partial record struct WrId
{
    public static bool IsValid(int value) => value >= 1;
}

public sealed record Wrap<T>(T Item);

[Verifiable]
public static class GenWrap
{
    public static int Raw(WrId id)
    {
        Spec.Ensures<int>(r => r >= 1, "An id is at least 1.");
        return id.Value;
    }

    public static int Caller(int x)
    {
        Spec.Ensures<int>(r => r >= 1, "Still at least 1.");
        return Raw(new Wrap<WrId>(new WrId(x)).Item);
    }
}
