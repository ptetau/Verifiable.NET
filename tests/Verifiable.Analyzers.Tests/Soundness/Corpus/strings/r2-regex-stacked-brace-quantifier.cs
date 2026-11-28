// a{2}{3} is a nested quantifier in .NET (the constructor throws); the model reads the second {3} as literal text.
// @expect: rejected
// @witness: Corpus.Strings.StackedBrace.Is("aa{3}")
using System.Text.RegularExpressions;
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class StackedBrace
{
    public static bool Is(string s)
    {
        Spec.Requires(s == "aa{3}", "Literal braces.");
        Spec.Ensures<bool>(r => r, "Claims the second braces are literal.");
        return Regex.IsMatch(s, "^a{2}{3}\\z");
    }
}
