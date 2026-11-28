// A correct program: constructor after a Requires, and From, both give valid aliases.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Types;

[TypeAlias<int>("Ids start at 1.")]
public readonly partial record struct KId
{
    public static bool IsValid(int value) => value >= 1;
}

[Verifiable]
public static class KIdUse
{
    public static int Raw(KId id)
    {
        Spec.Ensures<int>(r => r >= 1, "An id is at least 1.");
        return id.Value;
    }

    public static int Caller(int x)
    {
        Spec.Requires(x >= 1 && x <= 1000, "Valid small raw id.");
        Spec.Ensures<int>(r => r >= 1, "Still at least 1.");
        return Raw(new KId(x)) + Raw(KId.From(x));
    }
}
