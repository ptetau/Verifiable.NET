// Inside a class, \b is the backspace character U+0008.
// @expect: rejected
// @witness: Corpus.Strings.ClassBackspace.Is("\b")
using System.Text.RegularExpressions;
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class ClassBackspace
{
    public static bool Is(string s)
    {
        Spec.Requires(s == "\b", "Backspace.");
        Spec.Ensures<bool>(r => !r, "Claims [\\b] does not match backspace.");
        return Regex.IsMatch(s, "^[\\b]\\z");
    }
}
