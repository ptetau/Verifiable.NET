// Correct: a const pattern built by concatenation is a compile-time constant.
// @expect: proved
using System.Text.RegularExpressions;
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class ConstPattern
{
    private const string P = "^" + "a{2}" + "\\z";

    public static bool Is(string s)
    {
        Spec.Requires(s != null, "Text is given.");
        Spec.Ensures<bool>(r => r == (s == "aa"), "Exactly two a.");
        return Regex.IsMatch(s, P);
    }
}
