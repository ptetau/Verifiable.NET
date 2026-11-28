// string.StartsWith(string) with no StringComparison is culture-sensitive: ICU ignores "\0", so "abc".StartsWith("\0") is true.
// @expect: rejected
// @known-bug: StartsWith(string) without a StringComparison is culture-sensitive (ICU ignores \0) but is modelled as ordinal str.prefixof
// @witness: Corpus.Strings.CultureStartsWith.StartsWithNul("abc")
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class CultureStartsWith
{
    public static bool StartsWithNul(string s)
    {
        Spec.Requires(s != null, "Text is given.");
        Spec.Ensures<bool>(r => !r || s.Contains('\0'), "Only text holding a NUL can start with one.");
        return s.StartsWith("\0");
    }
}
