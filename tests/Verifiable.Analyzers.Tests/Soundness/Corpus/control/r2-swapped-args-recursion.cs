// A(n, m) recurses as A(m, n): the callee's measure is m, which is not below n; A(1, 5) and A(5, 1) swap forever.
// @expect: rejected
// @witness: Corpus.Control.Swapped.A(1, 5)
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class Swapped
{
    public static int A(int n, int m)
    {
        Spec.Requires(n >= 0 && n <= 5 && m >= 0 && m <= 5, "Small inputs.");
        Spec.Ensures<int>(r => r == 0, "Zero.");
        Spec.Decreases(n, "n.");
        if (n == 0)
        {
            return 0;
        }
        return A(m, n);
    }
}
