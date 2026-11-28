// [z-a] is a reversed range: the Regex constructor throws.
// @expect: rejected
// @witness: Corpus.Strings.Reversed.Is("a")
using System.Text.RegularExpressions;
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class Reversed
{
    public static bool Is(string s)
    {
        Spec.Requires(s != null, "Text is given.");
        Spec.Ensures<bool>(r => !r, "Claims nothing matches.");
        return Regex.IsMatch(s, "^[z-a]\\z");
    }
}
