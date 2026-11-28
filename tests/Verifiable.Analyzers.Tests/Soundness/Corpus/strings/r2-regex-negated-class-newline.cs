// Correct: [^a] matches a newline (only . excludes it), and \z does not allow a trailing newline.
// @expect: proved
using System.Text.RegularExpressions;
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class NegClass
{
    public static bool Is(string s)
    {
        Spec.Requires(s == "\n", "A newline.");
        Spec.Ensures<bool>(r => r, "Matches [^a].");
        return Regex.IsMatch(s, "^[^a]\\z");
    }

    public static bool Dot(string s)
    {
        Spec.Requires(s == "\n", "A newline.");
        Spec.Ensures<bool>(r => !r, "Does not match . without Singleline.");
        return Regex.IsMatch(s, "^.\\z");
    }
}
