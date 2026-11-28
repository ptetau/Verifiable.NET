// A correct program: nested tuple equality is elementwise.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Types;

[Verifiable]
public static class NestedTupleOk
{
    public static bool Same(int x, int y, int z)
    {
        Spec.Ensures<bool>(r => r == (x == y), "Equal exactly when x == y.");
        return ((x, y), z) == ((y, x), z);
    }
}
