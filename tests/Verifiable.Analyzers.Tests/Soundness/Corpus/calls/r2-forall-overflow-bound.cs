// Exists(0, n + 1, ...) with n = int.MaxValue: n + 1 wraps negative, the range is empty and Exists is false at run time. With unbounded integers the analyzer would think i == n is in range.
// @expect: rejected
// @witness: Corpus.Calls.R2QOver.Check(2147483647)
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public static class R2QOver
{
    public static int Check(int n)
    {
        Spec.Requires(n >= 0, "Non-negative.");
        Spec.Ensures<int>(r => Spec.Exists(0, n + 1, i => i == n), "n is in range.");
        return n;
    }
}
