// Sum(n) = 0 + 1 + ... + n; the recursion goes down by one and stops at zero.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class RecursiveSum
{
    public static int Sum(int n)
    {
        Spec.Requires(n >= 0 && n <= 200, "Small non-negative input.");
        Spec.Ensures<int>(r => 2 * r == n * (n + 1), "Triangle number.");
        if (n == 0)
        {
            return 0;
        }
        return n + Sum(n - 1);
    }
}
