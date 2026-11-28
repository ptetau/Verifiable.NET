// RegexOptions.ECMAScript may only be combined with IgnoreCase, Multiline and Compiled; with Singleline the call throws.
// @expect: rejected
// @witness: Corpus.Strings.RegexBadOptions.IsA("a")
using System.Text.RegularExpressions;
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class RegexBadOptions
{
    public static bool IsA(string s)
    {
        Spec.Requires(s != null, "Text is given.");
        Spec.Ensures<bool>(r => r == (s == "a"), "Matches exactly \"a\".");
        return Regex.IsMatch(s, "^a\\z", RegexOptions.ECMAScript | RegexOptions.Singleline);
    }
}
