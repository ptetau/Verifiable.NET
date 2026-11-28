// .NET rejects an escaped word character: Regex.IsMatch(s, @"\_") throws ArgumentException ("Unrecognized escape sequence").
// @expect: rejected
// @witness: Corpus.Strings.RegexBadEscape.IsUnderscore("_")
using System.Text.RegularExpressions;
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class RegexBadEscape
{
    public static bool IsUnderscore(string s)
    {
        Spec.Requires(s != null, "Text is given.");
        Spec.Ensures<bool>(r => r == (s == "_"), "Matches exactly one underscore.");
        return Regex.IsMatch(s, "^\\_\\z");
    }
}
