// Correct: Join(",", null, null) is just the separator.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class JoinNull2
{
    public static string Make(string? a, string? b)
    {
        Spec.Requires(a == null && b == null, "Both null.");
        Spec.Ensures<string>(r => r == ",", "Just the separator.");
        return string.Join(",", a, b);
    }
}
