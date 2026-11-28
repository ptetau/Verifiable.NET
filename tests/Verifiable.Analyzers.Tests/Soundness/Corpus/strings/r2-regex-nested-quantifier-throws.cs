// `a**` is a nested quantifier: the Regex constructor throws.
// @expect: rejected
// @witness: Corpus.Strings.NestedQ.Is("a")
using System.Text.RegularExpressions;
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class NestedQ
{
    public static bool Is(string s)
    {
        Spec.Requires(s != null, "Text is given.");
        Spec.Ensures<bool>(r => r == (s.Length == 0), "Claims it matches only the empty string.");
        return Regex.IsMatch(s, "^a**\\z");
    }
}
