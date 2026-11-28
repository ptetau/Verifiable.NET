// Correct: without ECMAScript, \s matches the no-break space.
// @expect: proved
using System.Text.RegularExpressions;
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class UniSpace
{
    public static bool Is(string s)
    {
        Spec.Requires(s == " ", "No-break space.");
        Spec.Ensures<bool>(r => r, "\\s matches no-break space.");
        return Regex.IsMatch(s, "^\\s\\z");
    }

    public static bool IsNot(string s)
    {
        Spec.Requires(s == " ", "No-break space.");
        Spec.Ensures<bool>(r => !r, "ECMAScript \\s does not.");
        return Regex.IsMatch(s, "^\\s\\z", RegexOptions.ECMAScript);
    }
}
