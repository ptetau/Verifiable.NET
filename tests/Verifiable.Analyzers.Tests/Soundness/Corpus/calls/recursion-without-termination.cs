// A method that only calls itself never returns, yet its postcondition "r > x" is "proved" from its own contract.
// The loop rules ask for a Decreases measure; recursion asks for none.
// @expect: rejected
// @witness: Corpus.Calls.Forever.Spin(1)
// @known-bug: Recursive calls assume the callee postcondition with no termination measure, so a method that never returns proves any satisfiable postcondition.
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public static class Forever
{
    public static int Spin(int x)
    {
        Spec.Requires(x >= 0 && x <= 1000, "Small inputs only.");
        Spec.Ensures<int>(r => r > x, "Grows (vacuously: it never returns).");
        return Spin(x);
    }
}
