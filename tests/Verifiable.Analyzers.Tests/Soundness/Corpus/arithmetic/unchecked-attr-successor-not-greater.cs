// Under [UncheckedArithmetic] x + 1 wraps at int.MaxValue, so the successor is not always bigger.
// @expect: rejected
// @witness: Corpus.Arithmetic.UncheckedNext.Next(int.MaxValue)
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
[UncheckedArithmetic]
public static class UncheckedNext
{
    public static int Next(int x)
    {
        Spec.Ensures<int>(r => r > x, "The successor is bigger.");
        return x + 1;
    }
}
