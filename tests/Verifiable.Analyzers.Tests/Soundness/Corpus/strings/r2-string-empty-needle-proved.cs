// Correct: every ordinal search for "" succeeds.
// @expect: proved
using System;
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class EmptyNeedle
{
    public static bool All(string s)
    {
        Spec.Requires(s != null, "Text is given.");
        Spec.Ensures<bool>(r => r, "All succeed.");
        return s.Contains("") && s.StartsWith("", StringComparison.Ordinal) && s.EndsWith("", StringComparison.Ordinal);
    }
}
