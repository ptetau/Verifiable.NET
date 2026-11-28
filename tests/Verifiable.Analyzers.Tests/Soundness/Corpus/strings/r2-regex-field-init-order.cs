// Static initializers run in text order: P is still null when Re is built, so the type initializer throws.
// @expect: rejected
// @witness: Corpus.Strings.InitOrder.Is("a")
using System.Text.RegularExpressions;
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class InitOrder
{
    private static readonly Regex Re = new Regex(P);
    private static readonly string P = "^a\\z";

    public static bool Is(string s)
    {
        Spec.Requires(s != null, "Text is given.");
        Spec.Ensures<bool>(r => r == (s == "a"), "Claims the field matches a.");
        return Re.IsMatch(s);
    }
}
