// With NonBacktracking, `$` still matches before a final "\n".
// @expect: rejected
// @witness: Corpus.Strings.NbDollar.Is("a\n")
using System.Text.RegularExpressions;
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class NbDollar
{
    public static bool Is(string s)
    {
        Spec.Requires(s != null, "Text is given.");
        Spec.Ensures<bool>(r => r == (s == "a"), "Claims only a matches.");
        return Regex.IsMatch(s, "^a$", RegexOptions.NonBacktracking);
    }
}
