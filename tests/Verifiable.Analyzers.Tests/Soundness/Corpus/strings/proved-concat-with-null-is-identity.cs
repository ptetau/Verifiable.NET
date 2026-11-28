// Correct: concatenating null adds nothing, so "a" + s is "a" followed by s (or just "a").
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class ConcatNull
{
    public static string Prefix(string? s)
    {
        Spec.Ensures<string>(r => r.Length == 1 + (s == null ? 0 : s.Length), "Adds one character.");
        Spec.Ensures<string>(r => r.StartsWith('a'), "Starts with the prefix.");
        return "a" + s;
    }
}
