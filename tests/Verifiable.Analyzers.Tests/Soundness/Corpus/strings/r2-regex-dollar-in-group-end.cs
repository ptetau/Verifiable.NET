// `(?:a$)` puts the anchor inside a group; `^(?:a|b$)` means a, or b before the end: "b\n" matches.
// @expect: rejected
// @witness: Corpus.Strings.DollarGroup.Is("b\n")
using System.Text.RegularExpressions;
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class DollarGroup
{
    public static bool Is(string s)
    {
        Spec.Requires(s == "b\n", "b newline.");
        Spec.Ensures<bool>(r => !r, "Claims it does not match.");
        return Regex.IsMatch(s, "^(?:a|b$)");
    }
}
