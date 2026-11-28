// uint + int is long, so 0u + -1 is -1, not a wrapped value; a non-negative result is wrong.
// @expect: rejected
// @witness: Corpus.Arithmetic.R2UintPlusInt.Run(0u, -1)
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class R2UintPlusInt
{
    public static long Run(uint u, int i)
    {
        Spec.Ensures<long>(r => r >= 0, "Not negative.");
        return u + i;
    }
}
