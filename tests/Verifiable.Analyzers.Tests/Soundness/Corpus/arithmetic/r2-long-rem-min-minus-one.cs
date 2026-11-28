// long.MinValue % -1 throws OverflowException (the quotient does not fit), even though the remainder would be 0.
// @expect: rejected
// @witness: Corpus.Arithmetic.R2RemMin.Run(long.MinValue, -1L)
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
[UncheckedArithmetic]
public static class R2RemMin
{
    public static long Run(long x, long y)
    {
        Spec.Requires(y != 0, "No division by zero.");
        Spec.Ensures<long>(r => r > -1000, "Small.");
        return x % y;
    }
}
