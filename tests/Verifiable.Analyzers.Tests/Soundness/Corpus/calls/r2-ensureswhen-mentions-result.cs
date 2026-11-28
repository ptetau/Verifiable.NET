// EnsuresWhen's guard mentions the result via Spec.Result: "when the result is positive it equals 1" is false for x = 5.
// @expect: rejected
// @no-witness: the run-time checker does not evaluate Spec.Result in an EnsuresWhen guard (Run(5) returns 5 and the guard is real)
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public static class R2EWRes
{
    public static int Run(int x)
    {
        Spec.Requires(x >= 0 && x <= 100, "Small.");
        Spec.EnsuresWhen<int>(Spec.Result<int>() > 0, r => r == 1, "Positive results are 1.");
        return x;
    }
}
