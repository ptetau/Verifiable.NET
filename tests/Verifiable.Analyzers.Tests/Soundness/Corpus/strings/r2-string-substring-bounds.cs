// Substring(0, n) throws when n is larger than the text.
// @expect: rejected
// @witness: Corpus.Strings.Sub.Take("ab", 5)
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class Sub
{
    public static string Take(string s, int n)
    {
        Spec.Requires(s != null && n >= 0, "Valid input.");
        Spec.Ensures<string>(r => r.Length == n, "Claims length n.");
        return s.Substring(0, n);
    }
}
