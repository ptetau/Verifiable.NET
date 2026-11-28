// Correct: .NET's \w does not include U+200D (it only joins in \b boundaries).
// @expect: proved
using System.Text.RegularExpressions;
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class WordZwj
{
    public static bool Is(string s)
    {
        Spec.Requires(s == "‍", "Zero width joiner.");
        Spec.Ensures<bool>(r => !r, "\\w does not match it.");
        return Regex.IsMatch(s, "^\\w\\z");
    }
}
