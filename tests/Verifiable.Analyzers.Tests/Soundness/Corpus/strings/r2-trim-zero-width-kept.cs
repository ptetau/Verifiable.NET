// Correct: U+200B and U+180E are not whitespace for Trim, so a string of them is kept.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class TrimZw
{
    public static int Len(string s)
    {
        Spec.Requires(s == "​᠎", "Invisible, non-space characters.");
        Spec.Ensures<int>(r => r == 2, "Trim keeps both.");
        return s.Trim().Length;
    }
}
