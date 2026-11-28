// string.CompareOrdinal returns 0 only for equal strings; claiming "a" < "B" (ordinal) is false.
// @expect: rejected
// @witness: Corpus.Strings.Cmp.Lt("a", "B")
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class Cmp
{
    public static bool Lt(string a, string b)
    {
        Spec.Requires(a == "a" && b == "B", "Fixed input.");
        Spec.Ensures<bool>(r => r, "Claims a sorts before B.");
        return string.CompareOrdinal(a, b) < 0;
    }
}
