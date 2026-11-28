// Correct: under [CheckedArithmetic], c.ToString() is the one-unit string holding c, so it contains c.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class CheckedChar
{
    [CheckedArithmetic]
    public static bool SelfContains(char c)
    {
        Spec.Ensures<bool>(r => r, "A char's own string contains it.");
        return c.ToString().Contains(c);
    }
}
