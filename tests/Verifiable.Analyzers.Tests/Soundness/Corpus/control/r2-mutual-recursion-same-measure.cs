// A calls B with the same n and B calls A with the same n: neither measure drops, the pair never returns.
// @expect: rejected
// @witness: Corpus.Control.PingPong.A(1)
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class PingPong
{
    public static int A(int n)
    {
        Spec.Requires(n >= 0 && n <= 10, "Small inputs.");
        Spec.Ensures<int>(r => r == 0, "Zero.");
        Spec.Decreases(n, "n.");
        if (n == 0)
        {
            return 0;
        }
        return B(n);
    }

    public static int B(int n)
    {
        Spec.Requires(n >= 0 && n <= 10, "Small inputs.");
        Spec.Ensures<int>(r => r == 0, "Zero.");
        Spec.Decreases(n, "n.");
        return A(n);
    }
}
