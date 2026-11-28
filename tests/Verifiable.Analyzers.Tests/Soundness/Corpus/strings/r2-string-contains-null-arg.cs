// Contains(null) throws ArgumentNullException.
// @expect: rejected
// @witness: Corpus.Strings.ContainsNull.Has("a", null)
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class ContainsNull
{
    public static bool Has(string s, string? t)
    {
        Spec.Requires(s != null, "Text is given.");
        Spec.Ensures<bool>(r => r || !r, "Always returns.");
        return s.Contains(t!);
    }
}
