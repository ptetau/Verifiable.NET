// A correct program: a [Ghost] expression-bodied helper describes the result in the postcondition.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public static class GhostSpec
{
    [Ghost]
    public static int Sq(int x) => x * x;

    public static int Square(int x)
    {
        Spec.Requires(x >= -1000 && x <= 1000, "Small inputs only.");
        Spec.Ensures<int>(r => r == Sq(x) && r >= 0, "The square, never negative.");
        return x * x;
    }
}
