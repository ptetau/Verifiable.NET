// A correct program: under [UncheckedArithmetic] a compound x *= 2 wraps, so the result is even whatever x is.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
[UncheckedArithmetic]
public static class R2CompoundMul
{
    public static int Run(int x)
    {
        Spec.Ensures<int>(r => r % 2 == 0, "Even.");
        x *= 2;
        return x;
    }
}
