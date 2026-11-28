// Correct: `s?.Length > 0` is false for null, so s is non-null inside the branch.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class LiftedGuard
{
    public static string TrimIfAny(string? s)
    {
        Spec.Ensures<string>(r => r != null, "Never null.");
        if (s?.Length > 0)
        {
            return s.Trim();
        }
        return "";
    }
}
