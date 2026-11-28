// Regex pattern from an expression-bodied property that builds a different string each call.
// @expect: rejected
// @witness: Corpus.Strings.PropRe.Is("a")
using System.Text.RegularExpressions;
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class PropRe
{
    private static Regex Re => new Regex("^b\\z");

    public static bool Is(string s)
    {
        Spec.Requires(s != null, "Text is given.");
        Spec.Ensures<bool>(r => r == (s == "a"), "Claims a.");
        return Re.IsMatch(s);
    }
}
