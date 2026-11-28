// F never returns (it calls itself with the same argument forever), yet its own postcondition "r == 0" is used for the recursive call.
// @expect: rejected
// @witness: Corpus.Control.SelfRecursion.F(1)
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class SelfRecursion
{
    public static int F(int x)
    {
        Spec.Requires(x >= 0, "Non-negative input.");
        Spec.Ensures<int>(r => r == 0, "Always zero.");
        return F(x) * 2;
    }
}
