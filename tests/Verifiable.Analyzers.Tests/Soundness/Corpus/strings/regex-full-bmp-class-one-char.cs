// Correct: every UTF-16 code unit is in [\u0000-￿], so a one-unit string always matches.
// @expect: proved
using System.Text.RegularExpressions;
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class RegexBmp
{
    public static bool OneUnit(string s)
    {
        Spec.Requires(s != null && s.Length == 1, "One code unit.");
        Spec.Ensures<bool>(r => r, "Always matches.");
        return Regex.IsMatch(s, "^[\\u0000-\\uFFFF]\\z");
    }
}
