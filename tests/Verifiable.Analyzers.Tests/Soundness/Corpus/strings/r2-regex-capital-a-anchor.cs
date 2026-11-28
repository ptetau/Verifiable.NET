// Correct: \A is the start of the text, so \Aab\z matches exactly "ab".
// @expect: proved
using System.Text.RegularExpressions;
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class CapA
{
    public static bool Is(string s)
    {
        Spec.Requires(s != null, "Text is given.");
        Spec.Ensures<bool>(r => r == (s == "ab"), "Exactly ab.");
        return Regex.IsMatch(s, "\\Aab\\z");
    }
}
