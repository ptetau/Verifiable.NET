// The callee requires a quantified fact; the caller establishes only a weaker one.
// @expect: rejected
// @witness: Corpus.Calls.R2QReq.Use(1)
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public static class R2QReq
{
    public static int NeedsPositives(int n)
    {
        Spec.Requires(n >= 0 && n < 10, "Small.");
        Spec.Requires(Spec.ForAll(0, n, i => i > 0), "All indices positive.");
        return n;
    }

    public static int Use(int k)
    {
        Spec.Requires(k >= 0 && k < 10, "Small.");
        return NeedsPositives(k);
    }
}
