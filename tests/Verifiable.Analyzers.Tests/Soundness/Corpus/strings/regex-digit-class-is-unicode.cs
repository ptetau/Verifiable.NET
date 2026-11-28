// Without RegexOptions.ECMAScript, \d matches every Unicode decimal digit, e.g. ARABIC-INDIC DIGIT THREE (U+0663).
// @expect: rejected
// @witness: Corpus.Strings.RegexDigit.IsOneDigit("٣")
using System.Text.RegularExpressions;
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class RegexDigit
{
    public static bool IsOneDigit(string s)
    {
        Spec.Requires(s != null, "Text is given.");
        Spec.Ensures<bool>(r => r == Regex.IsMatch(s, "^[0-9]\\z"), "Claims \\d is ASCII 0-9.");
        return Regex.IsMatch(s, "^\\d\\z");
    }
}
