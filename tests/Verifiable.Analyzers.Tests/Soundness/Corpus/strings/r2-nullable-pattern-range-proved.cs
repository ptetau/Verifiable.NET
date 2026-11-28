// Correct: a relational `and` pattern on int? is false for null.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class NullablePattern
{
    public static bool InRange(int? x)
    {
        Spec.Ensures<bool>(r => r == (x.HasValue && x.Value > 3 && x.Value < 10), "Strictly between 3 and 10.");
        return x is > 3 and < 10;
    }

    public static bool Present(int? x, int? y)
    {
        Spec.Ensures<bool>(r => r == (x.HasValue && y.HasValue && x.Value < y.Value), "Both present and ordered.");
        return x is not null && y is not null && (x ?? 0) < (y ?? 0);
    }
}
