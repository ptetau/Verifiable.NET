// int.MinValue / -1 throws OverflowException even in an unchecked context.
// @expect: rejected
// @witness: Corpus.Arithmetic.R2DivMin.Run(int.MinValue, -1)
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
[UncheckedArithmetic]
public static class R2DivMin
{
    public static int Run(int x, int y)
    {
        Spec.Requires(y != 0, "No division by zero.");
        Spec.Ensures<int>(r => r * y == x || r * y != x, "Always true if it returns.");
        return x / y;
    }
}
