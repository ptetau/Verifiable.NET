// Join with a null element: it is an empty string, but the separators remain. Claim says the result is empty.
// @expect: rejected
// @witness: Corpus.Strings.JoinNull.Make(null, null)
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class JoinNull
{
    public static string Make(string? a, string? b)
    {
        Spec.Requires(a == null && b == null, "Both null.");
        Spec.Ensures<string>(r => r.Length == 0, "Claims an empty result.");
        return string.Join(",", a, b);
    }
}
