// Control: under [CheckedArithmetic], (int) of a long keeps the low 32 bits, so 2^31 becomes negative. Must be rejected.
// @expect: rejected
// @witness: Corpus.Arithmetic.IntOfLongChecked.Narrow(2147483648L)
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
[CheckedArithmetic]
public static class IntOfLongChecked
{
    public static int Narrow(long x)
    {
        Spec.Requires(x >= 0, "Non-negative input.");
        Spec.Ensures<int>(r => r >= 0, "The result is not negative.");
        return (int)x;
    }
}
