// The local function's parameter x shadows the outer x, so Twice(x + 1) is 2 * (x + 1).
// @expect: proved
// @known-bug: False rejection: a call to a local function gets a fresh, unconstrained result (its expression body is never used), so even Twice(x + 1) == 2 * x + 2 cannot be proved.
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class Shadowing
{
    public static int Run(int x)
    {
        Spec.Requires(x >= -1000 && x <= 1000, "Small inputs.");
        Spec.Ensures<int>(r => r == 2 * x + 2, "Doubles x + 1.");
        static int Twice(int x) => x * 2;
        return Twice(x + 1);
    }
}
