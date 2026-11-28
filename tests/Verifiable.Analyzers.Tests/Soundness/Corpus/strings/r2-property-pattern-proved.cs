// Correct: a property pattern on Length implies non-null.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class PropPat2
{
    public static bool Long(string? s)
    {
        Spec.Ensures<bool>(r => r == (s != null && s.Length > 2), "Non-null and long.");
        return s is { Length: > 2 };
    }

    public static bool Known(string? s)
    {
        Spec.Ensures<bool>(r => r == (s == "ab" || s == "cd"), "One of two.");
        return s is "ab" or "cd";
    }
}
