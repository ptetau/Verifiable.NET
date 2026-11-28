// A computed property builds an alias without checking it; the caller treats the property value as valid.
// @expect: rejected
// @witness: Corpus.Types.CompAlias.Caller(0)
using Verifiable.Contracts;

namespace Corpus.Types;

[TypeAlias<int>("Ids start at 1.")]
public readonly partial record struct CId
{
    public static bool IsValid(int value) => value >= 1;
}

public sealed record Raw1(int N)
{
    public CId Id => new CId(N);
}

[Verifiable]
public static class CompAlias
{
    public static int Use(CId id)
    {
        Spec.Ensures<int>(r => r >= 1, "An id is at least 1.");
        return id.Value;
    }

    public static int Caller(int x)
    {
        Spec.Ensures<int>(r => r >= 1, "Still at least 1.");
        return Use(new Raw1(x).Id);
    }
}
