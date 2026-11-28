// Correct: char.ToString of any char is one UTF-16 unit, including lone surrogates, and matches the full-BMP class.
// @expect: proved
using System.Text.RegularExpressions;
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class CharStr
{
    public static bool Any(char c)
    {
        Spec.Ensures<bool>(r => r, "Always matches one unit.");
        var s = c.ToString();
        return s.Length == 1 && Regex.IsMatch(s, "^[\\u0000-\\uffff]\\z");
    }
}
