// `s!` does not check for null: s!.Length throws NullReferenceException for null.
// @expect: rejected
// @witness: Corpus.Strings.Bang.Len(null)
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class Bang
{
    public static int Len(string? s)
    {
        Spec.Ensures<int>(r => r >= 0, "Length is non-negative.");
        return s!.Length;
    }
}
