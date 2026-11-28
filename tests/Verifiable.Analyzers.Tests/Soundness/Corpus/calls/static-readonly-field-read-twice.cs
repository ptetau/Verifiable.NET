// Reading the same static readonly field twice gives the same value, so K - K is 0. K is only known to be some
// int, so x + K may wrap: [UncheckedArithmetic] makes wrapping legal, and x + K - K is still exactly x.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
[UncheckedArithmetic]
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
