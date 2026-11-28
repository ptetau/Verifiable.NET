// [a-z-[aeiou]] matches exactly one character, so "b" matches (a union reading would also, but "-" or "[" would not).
// @expect: rejected
// @witness: Corpus.Strings.ClassSubtract2.IsConsonant("e")
using System.Text.RegularExpressions;
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class ClassSubtract2
{
    public static bool IsConsonant(string s)
    {
        Spec.Requires(s == "e", "A vowel.");
        Spec.Ensures<bool>(r => r, "Claims a vowel matches.");
        return Regex.IsMatch(s, "^[a-z-[aeiou]]\\z");
    }
}
