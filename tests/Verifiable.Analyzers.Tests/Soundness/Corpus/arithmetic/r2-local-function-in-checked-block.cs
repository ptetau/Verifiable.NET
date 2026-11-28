// The local function sits lexically in a checked block, so its a + 1 throws at int.MaxValue; the method is [UncheckedArithmetic] but the local function does not wrap.
// @expect: rejected
// @witness: Corpus.Arithmetic.R2LocalChecked.Run(int.MaxValue)
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
[UncheckedArithmetic]
public static class R2LocalChecked
{
    public static int Run(int x)
    {
        Spec.Ensures<int>(r => (x == int.MaxValue && r == int.MinValue) || r == x + 1, "Wraps at the top.");
        checked
        {
            int F(int a) => a + 1;
            return F(x);
        }
    }
}
