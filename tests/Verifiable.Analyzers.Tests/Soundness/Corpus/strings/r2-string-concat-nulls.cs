// Correct: a null string concatenates as empty text.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class ConcatNulls
{
    public static int Len(string? a, string? b)
    {
        Spec.Ensures<int>(r => r == (a == null ? 0 : a.Length) + (b == null ? 0 : b.Length), "Nulls count as empty.");
        return (a + b).Length;
    }
}
