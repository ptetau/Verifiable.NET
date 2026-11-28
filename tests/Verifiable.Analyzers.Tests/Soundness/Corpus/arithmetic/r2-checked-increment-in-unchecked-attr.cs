// x++ inside a checked block throws at int.MaxValue even in an [UncheckedArithmetic] method.
// @expect: rejected
// @witness: Corpus.Arithmetic.R2CheckedInc.Run(int.MaxValue)
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
[UncheckedArithmetic]
public static class R2CheckedInc
{
    public static int Run(int x)
    {
        Spec.Ensures<int>(r => r == int.MinValue || r == x + 1, "Wraps at the top.");
        checked
        {
            x++;
        }
        return x;
    }
}
