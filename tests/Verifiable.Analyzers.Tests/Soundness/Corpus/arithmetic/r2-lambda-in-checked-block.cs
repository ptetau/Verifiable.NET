// The lambda sits lexically in a checked block, so its a + 1 throws at int.MaxValue even though the method is [UncheckedArithmetic].
// @expect: rejected
// @witness: Corpus.Arithmetic.R2LambdaChecked.Run(int.MaxValue)
using System;
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
[UncheckedArithmetic]
public static class R2LambdaChecked
{
    public static int Run(int x)
    {
        Spec.Ensures<int>(r => (x == int.MaxValue && r == int.MinValue) || r == x + 1, "Wraps at the top.");
        checked
        {
            Func<int, int> f = a => a + 1;
            return f(x);
        }
    }
}
