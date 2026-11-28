// The running sum wraps for n == 100000 (the sum is 5 * 10^9), so the mathematical invariant is false at run time.
// @expect: rejected
// @witness: Corpus.Arithmetic.R2LoopWrap.Sum(100000)
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
[UncheckedArithmetic]
public static class R2LoopWrap
{
    public static int Sum(int n)
    {
        Spec.Requires(n >= 0 && n <= 100000, "Bounded.");
        Spec.Ensures<int>(r => r >= 0, "Not negative.");
        int s = 0;
        for (int i = 0; i < n; i++)
        {
            Spec.Decreases(n - i, "i moves toward n.");
            Spec.LoopInvariant(s >= 0 && i <= n, "Not negative.");
            s += i;
        }
        return s;
    }
}
