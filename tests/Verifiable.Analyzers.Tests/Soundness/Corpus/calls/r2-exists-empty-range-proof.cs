// Exists over an empty range is false; the function claims an index exists whenever n >= 0.
// @expect: rejected
// @witness: Corpus.Calls.R2QEmpty.Has(0)
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public static class R2QEmpty
{
    public static bool Has(int n)
    {
        Spec.Requires(n >= 0 && n < 10, "Small.");
        Spec.Ensures<bool>(r => r == Spec.Exists(0, n, i => i >= 0), "Exact.");
        return true;
    }
}
