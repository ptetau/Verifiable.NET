// Without anchors IsMatch searches inside the text: "xaby" matches "ab", so the length is not 2.
// @expect: rejected
// @witness: Corpus.Strings.Unanch.Is("xaby")
using System.Text.RegularExpressions;
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class Unanch
{
    public static bool Is(string s)
    {
        Spec.Requires(s != null, "Text is given.");
        Spec.Ensures<bool>(r => !r || s.Length == 2, "Claims a match has length 2.");
        return Regex.IsMatch(s, "ab");
    }
}
