// Correct: s?.Length ?? -1 is -1 for null and the length otherwise.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class NullConditional
{
    public static int LengthOrMinusOne(string? s)
    {
        Spec.Ensures<int>(r => r == (s == null ? -1 : s.Length), "Length, or -1 for null.");
        return s?.Length ?? -1;
    }
}
