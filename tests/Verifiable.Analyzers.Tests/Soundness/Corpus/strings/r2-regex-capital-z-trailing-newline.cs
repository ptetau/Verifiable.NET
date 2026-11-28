// `\Z` matches at the end or before a final "\n".
// @expect: rejected
// @witness: Corpus.Strings.CapZ.Is("a\n")
using System.Text.RegularExpressions;
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class CapZ
{
    public static bool Is(string s)
    {
        Spec.Requires(s != null, "Text is given.");
        Spec.Ensures<bool>(r => r == (s == "a"), "Claims only a matches.");
        return Regex.IsMatch(s, "^a\\Z");
    }
}
