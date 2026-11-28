// `a\n$` matches "a\n" and also "a\n\n" (the $ may sit before the final newline).
// @expect: rejected
// @witness: Corpus.Strings.NlDollar.Is("a\n\n")
using System.Text.RegularExpressions;
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class NlDollar
{
    public static bool Is(string s)
    {
        Spec.Requires(s != null, "Text is given.");
        Spec.Ensures<bool>(r => r == (s == "a\n"), "Claims only a-newline matches.");
        return Regex.IsMatch(s, "^a\n$");
    }
}
