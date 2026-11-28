// Under ECMAScript, \s is only [ \f\n\r\t\v]; U+00A0 does not match. Claim says it does.
// @expect: rejected
// @witness: Corpus.Strings.EcmaSpace.Is(" ")
using System.Text.RegularExpressions;
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class EcmaSpace
{
    public static bool Is(string s)
    {
        Spec.Requires(s == " ", "No-break space.");
        Spec.Ensures<bool>(r => r, "Claims \\s matches no-break space.");
        return Regex.IsMatch(s, "^\\s\\z", RegexOptions.ECMAScript);
    }
}
