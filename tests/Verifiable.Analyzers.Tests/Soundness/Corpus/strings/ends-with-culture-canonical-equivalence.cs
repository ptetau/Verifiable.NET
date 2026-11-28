// string.EndsWith(string) with no StringComparison is culture-sensitive: "é" ends with "é" (canonically equal), though it is one code unit long.
// @expect: rejected
// @known-bug: EndsWith(string) without a StringComparison is culture-sensitive (canonical equivalence) but is modelled as ordinal str.suffixof
// @witness: Corpus.Strings.CultureEndsWith.EndsWithDecomposedAcute("é")
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class CultureEndsWith
{
    public static bool EndsWithDecomposedAcute(string s)
    {
        Spec.Requires(s != null, "Text is given.");
        Spec.Ensures<bool>(r => !r || s.Length >= 2, "A two-unit suffix needs at least two units.");
        return s.EndsWith("é");
    }
}
