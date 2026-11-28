// Arguments are written out of order; the unchecked alias is the one bound to the second parameter.
// @expect: rejected
// @witness: Corpus.Types.NamedArgs.Caller(0)
using Verifiable.Contracts;

namespace Corpus.Types;

[TypeAlias<int>("Ids start at 1.")]
public readonly partial record struct AId
{
    public static bool IsValid(int value) => value >= 1;
}

[Verifiable]
public static class NamedArgs
{
    public static int Second(AId first, AId second)
    {
        Spec.Ensures<int>(r => r >= 1, "An id is at least 1.");
        return second.Value;
    }

    public static int Caller(int x)
    {
        Spec.Ensures<int>(r => r >= 1, "Still at least 1.");
        return Second(second: new AId(x), first: AId.From(1));
    }
}
