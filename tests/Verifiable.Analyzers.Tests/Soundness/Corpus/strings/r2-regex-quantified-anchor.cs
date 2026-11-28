// In .NET a quantifier after the ^ anchor is a quantifier (on the anchor), not literal text, so "{2}a" does not match ^{2}a\z.
// @expect: rejected
// @known-bug: a {n} quantifier right after the ^ anchor is modelled as literal text, but .NET parses it as a quantifier on the anchor, so the analyzer proves a false claim
// @witness: Corpus.Strings.QuantAnchor.Is("{2}a")
using System.Text.RegularExpressions;
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class QuantAnchor
{
    public static bool Is(string s)
    {
        Spec.Requires(s == "{2}a", "Literal braces.");
        Spec.Ensures<bool>(r => r, "Claims the braces are literal.");
        return Regex.IsMatch(s, "^{2}a\\z");
    }
}
