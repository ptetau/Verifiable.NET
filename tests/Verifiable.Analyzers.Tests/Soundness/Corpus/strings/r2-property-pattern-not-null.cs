// `s is not { Length: > 2 }` is true for null.
// @expect: rejected
// @witness: Corpus.Strings.PropPat.Short(null)
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class PropPat
{
    public static bool Short(string? s)
    {
        Spec.Ensures<bool>(r => !r || s != null, "Claims a match is non-null.");
        return s is not { Length: > 2 };
    }
}
