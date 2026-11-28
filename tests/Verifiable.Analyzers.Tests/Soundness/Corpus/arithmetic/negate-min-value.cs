// -int.MinValue wraps to int.MinValue, so the negation of a negative number is not always positive.
// @expect: rejected
// @witness: Corpus.Arithmetic.NegateMin.Flip(int.MinValue)
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class NegateMin
{
    public static int Flip(int x)
    {
        Spec.Requires(x < 0, "Negative input.");
        Spec.Ensures<int>(r => r > 0, "The negation is positive.");
        return -x;
    }
}
