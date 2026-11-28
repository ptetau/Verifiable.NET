// The measure n * 2 wraps negative for n >= 2^30.
// @known-bug: Recursive Decreases(n * 2) is accepted although n * 2 wraps negative for n >= 2^30 (measure arithmetic is not checked for int overflow)
// @expect: rejected
// @witness: Corpus.Control.RecWrap.F(1073741824)
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class RecWrap
{
    public static int F(int n)
    {
        Spec.Requires(n >= 0, "Non-negative.");
        Spec.Ensures<int>(r => r == 0, "Zero.");
        Spec.Decreases(n * 2, "Twice n.");
        if (n == 0)
        {
            return 0;
        }
        return F(n - 1);
    }
}
