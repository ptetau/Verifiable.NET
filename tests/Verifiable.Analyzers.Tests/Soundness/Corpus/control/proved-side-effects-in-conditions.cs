// The right side of || runs only when the left is false, and only the chosen arm of ?: runs.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class SideEffects
{
    public static int Run(int x)
    {
        Spec.Requires(x >= -1000 && x <= 1000, "Small inputs.");
        Spec.Ensures<int>(r => r == (x > 0 ? 100 : 1) + (x > 5 ? 20 : -20), "Counts which side effects ran.");
        var k = 0;
        var c = 0;
        if (x > 0 || (k = 1) == 1)
        {
            c = x > 5 ? (c = c + 20) : (c = c - 20);
        }
        return (k == 0 ? 100 : 1) + c;
    }
}
