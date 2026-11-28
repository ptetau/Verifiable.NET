// The nested [CheckedArithmetic] type overrides its [UncheckedArithmetic] parent, so x + 1 throws at int.MaxValue instead of wrapping.
// @expect: rejected
// @no-witness: the attribute only sets the proof obligation; the compiled code wraps at run time, so nothing breaks when run
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
[UncheckedArithmetic]
public static class R2OuterU2
{
    [CheckedArithmetic]
    public static class InnerC
    {
        public static int Next(int x)
        {
            Spec.Ensures<int>(r => (x == int.MaxValue && r == int.MinValue) || r == x + 1, "Wraps at the top.");
            return x + 1;
        }
    }
}
