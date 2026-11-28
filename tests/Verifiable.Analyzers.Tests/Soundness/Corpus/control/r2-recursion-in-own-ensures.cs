// The postcondition mentions F(x - 1). F(1) returns 0 but the contract says it equals F(0) + 1 == 1.
// @expect: rejected
// @witness: Corpus.Control.SelfInEnsures.F(1)
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class SelfInEnsures
{
    public static int F(int x)
    {
        Spec.Requires(x >= 0 && x <= 10, "Small inputs.");
        Spec.Ensures<int>(r => x == 0 || r == F(x - 1) + 1, "One more than the previous.");
        Spec.Decreases(x, "x.");
        return 0;
    }
}
