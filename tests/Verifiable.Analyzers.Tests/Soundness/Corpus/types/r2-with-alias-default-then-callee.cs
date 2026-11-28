// A with-expression replaces a valid alias field by default(WId), and the result is passed on.
// @expect: rejected
// @witness: Corpus.Types.WithAlias.Caller(new Corpus.Types.WItem(Corpus.Types.WId.From(3), 1))
using Verifiable.Contracts;

namespace Corpus.Types;

[TypeAlias<int>("Ids start at 1.")]
public readonly partial record struct WId
{
    public static bool IsValid(int value) => value >= 1;
}

public sealed record WItem(WId Id, int N);

[Verifiable]
public static class WithAlias
{
    public static int Use(WItem it)
    {
        Spec.Ensures<int>(r => r >= 1, "An id is at least 1.");
        return it.Id.Value;
    }

    public static int Caller(WItem it)
    {
        Spec.Ensures<int>(r => r >= 1, "Still at least 1.");
        return Use(it with { Id = default });
    }
}
