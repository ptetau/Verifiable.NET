// Implies(Implies(a, b), c) is NOT Implies(a, Implies(b, c)): with r == 0 the inner implication holds and the consequent r > 0 fails.
// @expect: rejected
// @witness: Corpus.Calls.R2ImpWrong.Run(0)
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public static class R2ImpWrong
{
    public static int Run(int x)
    {
        Spec.Requires(x >= -100 && x <= 100, "Small.");
        Spec.Ensures<int>(r => Spec.Implies(Spec.Implies(r > 5, r > 3), r > 0), "Wrongly grouped.");
        return x;
    }
}
