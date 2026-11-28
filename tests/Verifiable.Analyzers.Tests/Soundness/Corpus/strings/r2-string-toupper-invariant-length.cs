// ToUpperInvariant keeps ß unchanged (simple case mapping), so the length is preserved; claiming it grows is wrong.
// @expect: rejected
// @witness: Corpus.Strings.Upper.Up("ß")
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class Upper
{
    public static string Up(string s)
    {
        Spec.Requires(s == "ß", "Sharp s.");
        Spec.Ensures<string>(r => r.Length == 2, "Claims SS.");
        return s.ToUpperInvariant();
    }
}
