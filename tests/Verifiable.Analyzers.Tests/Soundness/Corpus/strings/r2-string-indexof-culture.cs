// IndexOf(string) is culture-sensitive: an empty/ignorable needle semantics differ; claim IndexOf("") > 0 for non-empty text is false (it is 0).
// @expect: rejected
// @witness: Corpus.Strings.IdxOf.Find("abc")
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class IdxOf
{
    public static int Find(string s)
    {
        Spec.Requires(s != null && s.Length > 0, "Non-empty text.");
        Spec.Ensures<int>(r => r > 0, "Claims the empty needle is found after the start.");
        return s.IndexOf("");
    }
}
