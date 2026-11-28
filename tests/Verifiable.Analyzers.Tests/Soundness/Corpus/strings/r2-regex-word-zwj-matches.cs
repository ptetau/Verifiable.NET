// Claims \w matches U+200D (zero width joiner); it does not.
// @expect: rejected
// @witness: Corpus.Strings.WordZwj2.Is("‍")
using System.Text.RegularExpressions;
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class WordZwj2
{
    public static bool Is(string s)
    {
        Spec.Requires(s == "‍", "Zero width joiner.");
        Spec.Ensures<bool>(r => r, "Claims \\w matches it.");
        return Regex.IsMatch(s, "^\\w\\z");
    }
}
