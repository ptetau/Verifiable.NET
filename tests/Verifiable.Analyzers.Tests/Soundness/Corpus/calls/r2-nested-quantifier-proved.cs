// Nested quantifiers: for all i in [0,n) there exists j in [0,n) with j == i. True for any n.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public static class R2QNest
{
    public static int Run(int n)
    {
        Spec.Requires(n >= 0 && n < 100, "Small.");
        Spec.Ensures<int>(r => Spec.ForAll(0, n, i => Spec.Exists(0, n, j => j == i)), "Trivial.");
        return n;
    }
}
