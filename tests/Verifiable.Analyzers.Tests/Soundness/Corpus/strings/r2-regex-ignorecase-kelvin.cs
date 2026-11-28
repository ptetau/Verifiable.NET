// With IgnoreCase the Kelvin sign U+212A matches k (CultureInvariant makes this culture independent).
// @expect: rejected
// @witness: Corpus.Strings.Kelvin.IsK("K")
using System.Text.RegularExpressions;
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class Kelvin
{
    public static bool IsK(string s)
    {
        Spec.Requires(s != null, "Text is given.");
        Spec.Ensures<bool>(r => r == (s == "k" || s == "K"), "Claims only ASCII k and K match.");
        return Regex.IsMatch(s, "^k\\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
