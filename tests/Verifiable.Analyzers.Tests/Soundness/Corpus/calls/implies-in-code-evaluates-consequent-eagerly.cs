// Spec.Implies is an ordinary method at run time: both arguments are evaluated before the call, so 10 / x runs even
// when x == 0. The analyzer lowers the consequent only under the antecedent, so it misses the division by zero.
// @expect: rejected
// @witness: Corpus.Calls.ImpliesInCode.Check(0)
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public static class ImpliesInCode
{
    public static bool Check(int x)
    {
        Spec.Requires(x >= 0 && x <= 100, "Small inputs only.");
        Spec.Ensures<bool>(r => r, "Always true.");
        return Spec.Implies(x != 0, 10 / x >= 0);
    }
}
