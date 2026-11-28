// Correct: Trim() removes Unicode whitespace from both ends and never makes the text longer.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class TrimShrinks
{
    public static string Clean(string s)
    {
        Spec.Requires(s != null, "Text is given.");
        Spec.Ensures<string>(r => r.Length <= s.Length, "Trimming never grows the text.");
        Spec.Ensures<string>(r => r.Length == 0 == string.IsNullOrWhiteSpace(s), "Only blank text trims to nothing.");
        return s.Trim();
    }
}
