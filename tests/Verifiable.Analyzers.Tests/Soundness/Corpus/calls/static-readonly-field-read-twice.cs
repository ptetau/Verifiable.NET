// Reading the same static readonly field twice gives the same value, so K - K is 0.
// @expect: proved
// @known-bug: False rejection: each read of a static readonly field becomes a fresh unknown (LowerFieldReference binds a fresh symbol per read), so K - K is not 0.
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public static class ReadonlyField
{
    private static readonly int K = 5;

    public static int Zero(int x)
    {
        Spec.Requires(x >= 0 && x <= 10, "Small inputs only.");
        Spec.Ensures<int>(r => r == x, "Adds K and takes it away again.");
        return x + K - K;
    }
}
