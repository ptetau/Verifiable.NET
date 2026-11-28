// Interpolating an int adds its digits, so the length is not a.Length + 1.
// @expect: rejected
// @witness: Corpus.Strings.Interp.Tag("a", 100)
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class Interp
{
    public static string Tag(string a, int n)
    {
        Spec.Requires(a != null && n >= 0, "Valid input.");
        Spec.Ensures<string>(r => r.Length == a.Length + 1, "Claims one digit.");
        return $"{a}{n}";
    }
}
