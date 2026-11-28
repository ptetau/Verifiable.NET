// A correct program: unary minus on a uint gives a long, so -u does not wrap.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class R2UintNeg
{
    public static long Run(uint x)
    {
        Spec.Ensures<long>(r => r == -(long)x, "Negation in long.");
        return -x;
    }
}
