// Correct: .NET does not treat U+001F as whitespace, so Trim() keeps it (length 1).
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class TrimUnitSep
{
    public static int Len(string s)
    {
        Spec.Requires(s == "\u001f", "Just the unit separator.");
        Spec.Ensures<int>(r => r == 1, "Trim keeps the separator.");
        return s.Trim().Length;
    }
}
