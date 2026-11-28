// A [Ghost] method that loops is used in a contract; the ghost semantic must be the real value (sum 0..n-1), not unconstrained.
// @expect: rejected
// @witness: Corpus.Calls.R2GhostLoop.Use(4)
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public static class R2GhostLoop
{
    [Ghost]
    public static int Sum(int n)
    {
        Spec.Requires(n >= 0 && n <= 10, "Small.");
        int s = 0;
        for (int i = 0; i < n; i++)
        {
            Spec.LoopInvariant(i >= 0 && i <= n && s == i * (i - 1) / 2, "Triangular.");
            s += i;
        }

        return s;
    }

    public static int Use(int n)
    {
        Spec.Requires(n >= 0 && n <= 10, "Small.");
        Spec.Ensures<int>(r => r == Sum(n), "Matches.");
        return n * n;
    }
}
