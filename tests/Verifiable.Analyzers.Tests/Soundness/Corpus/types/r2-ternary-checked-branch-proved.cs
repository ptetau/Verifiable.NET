// A correct program: x > 0 makes the unchecked TId valid, and the other branch uses From.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Types;

[TypeAlias<int>("Ids start at 1.")]
public readonly partial record struct TOk
{
    public static bool IsValid(int value) => value >= 1;
}

[Verifiable]
public static class TernOk
{
    public static int Raw(TOk id)
    {
        Spec.Ensures<int>(r => r >= 1, "An id is at least 1.");
        return id.Value;
    }

    public static int Caller(int x)
    {
        Spec.Ensures<int>(r => r >= 1, "Still at least 1.");
        return Raw(x > 0 ? new TOk(x) : TOk.From(1));
    }
}
