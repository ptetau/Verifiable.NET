// A [PureAxiom] whose ensures is false in its own body is used inside another method's contract.
// @expect: rejected
// @no-witness: at run time Twice really returns 2a+1, so Use's postcondition holds; the program is wrong only because it trusts an axiom that is false, and a [PureAxiom] call inside a contract is refused (VER101)
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public static class R2AxCon
{
    [PureAxiom]
    public static int Twice(int x)
    {
        Spec.Requires(x >= 0 && x <= 100, "Small.");
        Spec.Ensures<int>(r => r == 2 * x, "Doubles.");
        return x + x + 1;
    }

    public static int Use(int a)
    {
        Spec.Requires(a >= 0 && a <= 100, "Small.");
        Spec.Ensures<int>(r => r == Twice(a), "Matches the axiom.");
        return a + a + 1;
    }
}
