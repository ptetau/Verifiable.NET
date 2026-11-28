// A local Regex is reassigned on one path; the analyzer must not use the initializer's pattern for both.
// @expect: rejected
// @witness: Corpus.Strings.LocalRe.Is("b", true)
using System.Text.RegularExpressions;
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class LocalRe
{
    public static bool Is(string s, bool useB)
    {
        Spec.Requires(s != null, "Text is given.");
        Spec.Ensures<bool>(r => r == (s == "a"), "Claims the local matches a.");
        var re = new Regex("^a\\z");
        if (useB)
        {
            re = new Regex("^b\\z");
        }
        return re.IsMatch(s);
    }
}
