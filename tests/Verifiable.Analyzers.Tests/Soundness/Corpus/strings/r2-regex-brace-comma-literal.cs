// `a{,3}` is not a quantifier in .NET: it matches the literal text "a{,3}".
// @expect: rejected
// @witness: Corpus.Strings.BraceLiteral.Is("a{,3}")
using System.Text.RegularExpressions;
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class BraceLiteral
{
    public static bool Is(string s)
    {
        Spec.Requires(s != null, "Text is given.");
        Spec.Ensures<bool>(r => !r || s.Length <= 3, "Claims a short match.");
        return Regex.IsMatch(s, "^a{,3}\\z");
    }
}
