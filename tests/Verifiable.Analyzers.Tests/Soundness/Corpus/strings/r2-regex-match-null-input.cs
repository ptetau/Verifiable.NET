// Regex.IsMatch(null, ...) throws ArgumentNullException, so a contract that holds for every string? breaks.
// @expect: rejected
// @witness: Corpus.Strings.NullInput.Is(null)
using System.Text.RegularExpressions;
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class NullInput
{
    public static bool Is(string? s)
    {
        Spec.Ensures<bool>(r => r || !r, "Always returns.");
        return Regex.Match(s!, "a").Success;
    }
}
