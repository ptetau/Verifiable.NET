// s.Length on a null string? throws NullReferenceException; the analyzer must ask for a non-null proof.
// @expect: rejected
// @known-bug: s.Length on a string? has no non-null obligation; null.Length is modelled as 0
// @witness: Corpus.Strings.NullLength.Len(null)
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class NullLength
{
    public static int Len(string? s)
    {
        Spec.Ensures<int>(r => r >= 0, "Lengths are never negative.");
        return s.Length;
    }
}
