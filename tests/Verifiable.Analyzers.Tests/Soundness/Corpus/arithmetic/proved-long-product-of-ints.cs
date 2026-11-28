// A correct program: widening to long before multiplying two ints cannot overflow.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
[CheckedArithmetic]
public static class WideProduct
{
    public static long Product(int a, int b)
    {
        Spec.Requires(a >= 0 && b >= 0, "Non-negative factors.");
        Spec.Ensures<long>(r => r >= 0 && r <= (long)int.MaxValue * int.MaxValue, "The product is non-negative and bounded.");
        return (long)a * b;
    }
}
