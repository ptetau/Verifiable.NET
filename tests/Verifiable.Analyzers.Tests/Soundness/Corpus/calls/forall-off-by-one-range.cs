// Spec.ForAll's end is exclusive: the range 0..n+1 includes i == n, where i < n is false.
// @expect: rejected
// @witness: Corpus.Calls.ForAllOffByOne.AllBelow(0)
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public static class ForAllOffByOne
{
    public static bool AllBelow(int n)
    {
        Spec.Requires(n >= 0 && n <= 50, "Small inputs only.");
        Spec.Ensures<bool>(r => r == Spec.ForAll(0, n + 1, i => i < n), "Every index up to n is below n.");
        return true;
    }
}
