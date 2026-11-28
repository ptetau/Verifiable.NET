// The unchecked helper wraps (1 << 30) * 2 to int.MinValue, so the default-mode caller's result is not always non-negative.
// @expect: rejected
// @witness: Corpus.Arithmetic.R2HelperWrap.Run(1073741824)
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class R2HelperWrap
{
    [UncheckedArithmetic]
    private static int Dbl(int x) => x * 2;

    public static int Run(int x)
    {
        Spec.Requires(x >= 0, "x is not negative.");
        Spec.Ensures<int>(r => r >= 0, "Not negative.");
        return Dbl(x);
    }
}
