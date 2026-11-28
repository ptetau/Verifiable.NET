// The Regex comes in as a parameter, so its pattern is unknown; the empty pattern matches "".
// @expect: rejected
// @witness: Corpus.Strings.ParamRe.Is(new System.Text.RegularExpressions.Regex(""), "")
using System.Text.RegularExpressions;
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class ParamRe
{
    public static bool Is(Regex re, string s)
    {
        Spec.Requires(s != null && re != null, "Given.");
        Spec.Ensures<bool>(r => !r || s.Length > 0, "Claims a match is non-empty.");
        return re.IsMatch(s);
    }
}
