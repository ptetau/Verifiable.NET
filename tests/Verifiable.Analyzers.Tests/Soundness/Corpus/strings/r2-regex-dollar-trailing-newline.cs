// `$` also matches before a final "\n", so "abc\n" matches ^abc$.
// @expect: rejected
// @witness: Corpus.Strings.DollarNl.Is("abc\n")
using System.Text.RegularExpressions;
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class DollarNl
{
    public static bool Is(string s)
    {
        Spec.Requires(s != null, "Text is given.");
        Spec.Ensures<bool>(r => r == (s == "abc"), "Claims only abc matches.");
        return Regex.IsMatch(s, "^abc$");
    }
}
