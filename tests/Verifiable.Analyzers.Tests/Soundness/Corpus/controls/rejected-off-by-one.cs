// A plainly wrong program: returning x cannot be greater than x.
// @expect: rejected
// @witness: Corpus.Controls.OffByOne.Next(0)
using Verifiable.Contracts;

namespace Corpus.Controls;

[Verifiable]
public static class OffByOne
{
    public static int Next(int x)
    {
        Spec.Requires(x >= 0, "Non-negative input.");
        Spec.Ensures<int>(r => r > x, "Result is bigger than the input.");
        return x;
    }
}
