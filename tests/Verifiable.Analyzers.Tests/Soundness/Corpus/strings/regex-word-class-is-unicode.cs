// Without RegexOptions.ECMAScript, \w matches Unicode letters such as "é".
// @expect: rejected
// @known-bug: RegexModel models \w as [A-Za-z0-9_] even without ECMAScript, where .NET matches Unicode letters, marks and connectors
// @witness: Corpus.Strings.RegexWord.IsOneWordChar("é")
using System.Text.RegularExpressions;
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class RegexWord
{
    public static bool IsOneWordChar(string s)
    {
        Spec.Requires(s != null, "Text is given.");
        Spec.Ensures<bool>(r => r == Regex.IsMatch(s, "^[A-Za-z0-9_]\\z"), "Claims \\w is ASCII.");
        return Regex.IsMatch(s, "^\\w\\z");
    }
}
