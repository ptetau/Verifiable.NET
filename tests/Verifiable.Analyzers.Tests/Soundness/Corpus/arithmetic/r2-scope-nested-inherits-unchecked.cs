// A correct program: a nested type with no attribute takes the nearest enclosing one, so it wraps like its [UncheckedArithmetic] parent.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
[UncheckedArithmetic]
public static class R2OuterUnchecked
{
    public static class Inner
    {
        public static int Next(int x)
        {
            Spec.Ensures<int>(r => (x == int.MaxValue && r == int.MinValue) || r == x + 1, "Wraps at the top.");
            return x + 1;
        }
    }
}
