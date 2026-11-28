// In a .NET regex, \012 is the octal escape for '\n', not NUL followed by "12".
// @expect: rejected
// @known-bug: RegexModel reads \0 as NUL and leaves the following octal digits as literals; .NET reads \012 as one octal escape
// @witness: Corpus.Strings.RegexOctal.IsNewline("\n")
using System.Text.RegularExpressions;
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class RegexOctal
{
    public static bool IsNewline(string s)
    {
        Spec.Requires(s != null, "Text is given.");
        Spec.Ensures<bool>(r => !r || s.Length == 3, "Claims the pattern is three characters long.");
        return Regex.IsMatch(s, "^\\012\\z");
    }
}
