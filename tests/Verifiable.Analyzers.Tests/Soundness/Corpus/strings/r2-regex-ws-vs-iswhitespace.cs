// Correct: ^\s*\z agrees with string.IsNullOrWhiteSpace (both use the Unicode whitespace set, which excludes U+001C..U+001F and U+200B).
// @expect: proved
using System.Text.RegularExpressions;
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class WsAgree
{
    public static bool Blank(string s)
    {
        Spec.Requires(s != null, "Text is given.");
        Spec.Ensures<bool>(r => r == Regex.IsMatch(s, "^\\s*\\z"), "Same notion of blank.");
        return string.IsNullOrWhiteSpace(s);
    }
}
