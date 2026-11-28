// Without [CheckedArithmetic], (int) of a long still keeps only the low 32 bits: (int)2^31 is int.MinValue.
// @expect: rejected
// @witness: Corpus.Arithmetic.IntOfLong.Narrow(2147483648L)
// @known-bug: Without [CheckedArithmetic], an unchecked (int) of a long is the identity, not a wrap to 32 bits.
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
