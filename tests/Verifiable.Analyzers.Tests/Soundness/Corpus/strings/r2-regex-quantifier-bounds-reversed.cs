// a{3,2} is invalid: the Regex constructor throws.
// @expect: rejected
// @witness: Corpus.Strings.BadBounds.Is("aa")
using System.Text.RegularExpressions;
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class BadBounds
{
    public static bool Is(string s)
    {
        Spec.Requires(s != null, "Text is given.");
        Spec.Ensures<bool>(r => !r, "Claims nothing matches.");
        return Regex.IsMatch(s, "^a{3,2}\\z");
    }
}
