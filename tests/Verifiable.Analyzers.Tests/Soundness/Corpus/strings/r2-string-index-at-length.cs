// s[s.Length] is out of range.
// @expect: rejected
// @witness: Corpus.Strings.IdxLen.At("ab", 2)
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class IdxLen
{
    public static char At(string s, int i)
    {
        Spec.Requires(s != null && i >= 0 && i <= s.Length, "Index in 0..Length.");
        Spec.Ensures<char>(r => r == r, "Trivial.");
        return s[i];
    }
}
