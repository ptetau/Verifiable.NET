// \cA is U+0001.
// @expect: rejected
// @witness: Corpus.Strings.CtrlEsc.Is("\u0001")
using System.Text.RegularExpressions;
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class CtrlEsc
{
    public static bool Is(string s)
    {
        Spec.Requires(s == "\u0001", "Control A.");
        Spec.Ensures<bool>(r => !r, "Claims \\cA does not match U+0001.");
        return Regex.IsMatch(s, "^\\cA\\z");
    }
}
