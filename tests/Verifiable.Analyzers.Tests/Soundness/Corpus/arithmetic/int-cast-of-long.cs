// Without [CheckedArithmetic], (int) of a long still keeps only the low 32 bits: (int)2^31 is int.MinValue.
// @expect: rejected
// @witness: Corpus.Arithmetic.IntOfLong.Narrow(2147483648L)
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class IntOfLong
{
    public static int Narrow(long x)
    {
        Spec.Requires(x >= 0, "Non-negative input.");
        Spec.Ensures<int>(r => r >= 0, "The result is not negative.");
        return (int)x;
    }
}
