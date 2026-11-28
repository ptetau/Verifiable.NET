// `s is var t` matches null too, so a true result does not mean s is non-null.
// @expect: rejected
// @witness: Corpus.Strings.VarPattern.Matches(null)
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class VarPattern
{
    public static bool Matches(string? s)
    {
        Spec.Ensures<bool>(r => !r || s != null, "Claims a var pattern rejects null.");
        return s is var t;
    }
}
