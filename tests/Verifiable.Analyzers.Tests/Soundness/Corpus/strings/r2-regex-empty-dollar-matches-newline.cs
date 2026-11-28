// ^$ matches "" and also "\n".
// @expect: rejected
// @witness: Corpus.Strings.EmptyAnchors.IsEmpty("\n")
using System.Text.RegularExpressions;
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class EmptyAnchors
{
    public static bool IsEmpty(string s)
    {
        Spec.Requires(s != null, "Text is given.");
        Spec.Ensures<bool>(r => r == (s.Length == 0), "Claims only the empty string matches.");
        return Regex.IsMatch(s, "^$");
    }
}
