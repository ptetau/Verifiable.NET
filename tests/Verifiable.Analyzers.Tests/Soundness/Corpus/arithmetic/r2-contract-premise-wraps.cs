// Spec.Requires(x + x >= 0) is arithmetic in the contract; at run time x + x wraps, so int.MinValue passes it (sum 0) although x is negative.
// @expect: rejected
// @known-bug: contract arithmetic is mathematical (unbounded) while the run-time contract wraps, so Requires(x + x >= 0) is assumed to imply x >= 0 and the false postcondition is proved
// @witness: Corpus.Arithmetic.R2PremiseWraps.Run(int.MinValue)
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class R2PremiseWraps
{
    public static int Run(int x)
    {
        Spec.Requires(x + x >= 0, "Twice x is not negative.");
        Spec.Ensures<int>(r => r >= 0, "The result is not negative.");
        return x;
    }
}
