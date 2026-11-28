// Correct: under [CheckedArithmetic], s.Length + 1 cannot overflow (a string holds at most about 2^30 code units).
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class CheckedLength
{
    [CheckedArithmetic]
    public static int LengthPlusOne(string s)
    {
        Spec.Requires(s != null, "Text is given.");
        Spec.Ensures<int>(r => r == s.Length + 1 && r > 0, "One more than the length.");
        return s.Length + 1;
    }
}
