// A correct program: the method's own [UncheckedArithmetic] wins over the [CheckedArithmetic] class, so the wrap is real.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
[CheckedArithmetic]
public static class R2MethodWins
{
    [UncheckedArithmetic]
    public static int Next(int x)
    {
        Spec.Ensures<int>(r => (x == int.MaxValue && r == int.MinValue) || r == x + 1, "Wraps at the top.");
        return x + 1;
    }
}
