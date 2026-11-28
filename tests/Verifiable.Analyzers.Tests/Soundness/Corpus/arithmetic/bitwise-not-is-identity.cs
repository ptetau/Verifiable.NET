// ~x flips every bit, so ~0 is -1: the result is never equal to x.
// @expect: rejected
// @witness: Corpus.Arithmetic.BitwiseNot.Flip(0)
// @known-bug: Unary ~ on an integer is lowered as the identity (CfgSsaLowerer.cs, IUnaryOperation falls through to "return uOp").
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class BitwiseNot
{
    public static int Flip(int x)
    {
        Spec.Ensures<int>(r => r == x, "Flipping the bits gives the same value.");
        return ~x;
    }
}
