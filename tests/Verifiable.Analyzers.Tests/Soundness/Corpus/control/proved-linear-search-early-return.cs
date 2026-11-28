// Returns the first i in [0, n) with i * i >= t, or n; the early return inside the loop must meet the postcondition too.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class Search
{
    public static int FirstSquareAtLeast(int n, int t)
    {
        Spec.Requires(n >= 0 && n <= 50 && t >= 0 && t <= 3000, "Small inputs.");
        Spec.Ensures<int>(r => r >= 0 && r <= n && (r == n || r * r >= t) && (r == 0 || (r - 1) * (r - 1) < t), "First square at least t.");
        var i = 0;
        while (i < n)
        {
            Spec.Decreases(n - i, "i moves toward n.");
            Spec.LoopInvariant(i >= 0 && i <= n && (i == 0 || (i - 1) * (i - 1) < t), "Every earlier square is below t.");
            if (i * i >= t)
            {
                return i;
            }
            i = i + 1;
        }
        return i;
    }
}
