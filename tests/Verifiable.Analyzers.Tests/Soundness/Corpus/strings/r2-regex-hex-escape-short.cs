// \x4g is an incomplete hex escape: .NET throws.
// @expect: rejected
// @witness: Corpus.Strings.HexShort.Is("A")
using System.Text.RegularExpressions;
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class HexShort
{
    public static bool Is(string s)
    {
        Spec.Requires(s != null, "Text is given.");
        Spec.Ensures<bool>(r => !r || s.Length > 0, "Trivial for a match.");
        return Regex.IsMatch(s, "^\\x4g\\z");
    }
}
