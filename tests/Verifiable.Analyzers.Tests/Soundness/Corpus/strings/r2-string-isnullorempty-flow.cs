// Correct: after !IsNullOrEmpty(s), s is non-null and non-empty.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class FlowEmpty
{
    public static int First(string? s)
    {
        Spec.Ensures<int>(r => r >= 0, "Non-negative.");
        if (string.IsNullOrEmpty(s))
        {
            return 0;
        }
        return s.Length - 1;
    }
}
