// Nested tuples compare elementwise: ((x, y), z) == ((y, x), z) holds when x == y.
// @expect: rejected
// @witness: Corpus.Types.NestedTuple.Same(1, 1, 5)
using Verifiable.Contracts;

namespace Corpus.Types;

[Verifiable]
public static class NestedTuple
{
    public static bool Same(int x, int y, int z)
    {
        Spec.Ensures<bool>(r => r == false, "Swapped inner pair differs.");
        return ((x, y), z) == ((y, x), z);
    }
}
