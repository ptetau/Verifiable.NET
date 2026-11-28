// `^.$` matches "a\n" because `$` can sit before the final newline.
// @expect: rejected
// @witness: Corpus.Strings.DotDollar.One("a\n")
using System.Text.RegularExpressions;
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class DotDollar
{
    public static bool One(string s)
    {
        Spec.Requires(s != null, "Text is given.");
        Spec.Ensures<bool>(r => !r || s.Length == 1, "Claims a match is one char.");
        return Regex.IsMatch(s, "^.$");
    }
}
