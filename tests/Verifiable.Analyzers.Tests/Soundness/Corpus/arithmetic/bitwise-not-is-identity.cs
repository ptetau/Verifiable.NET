// ~x flips every bit, so ~0 is -1: the result is never equal to x.
// @expect: rejected
// @witness: Corpus.Arithmetic.BitwiseNot.Flip(0)
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
