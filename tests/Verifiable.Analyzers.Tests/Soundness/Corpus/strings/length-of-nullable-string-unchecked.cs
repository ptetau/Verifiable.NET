// s.Length on a null string? throws NullReferenceException; the analyzer must ask for a non-null proof.
// @expect: rejected
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
