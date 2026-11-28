// `s?.Length ?? 0` is 0 for null and for "": claiming it is positive for non-null is wrong.
// @expect: rejected
// @witness: Corpus.Strings.NullCond.Len("")
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class NullCond
{
    public static int Len(string? s)
    {
        Spec.Requires(s != null, "Given.");
        Spec.Ensures<int>(r => r > 0, "Claims non-null is non-empty.");
        return s?.Length ?? 0;
    }
}
