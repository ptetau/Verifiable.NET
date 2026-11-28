// A correct program: nested tuple equality is elementwise.
// @expect: proved
// @known-bug: nested tuple equality ((x, y), z) == ((y, x), z) makes Z3 fail with a sort clash (Dt_ValueTuple_1 vs Dt_ValueTuple_2) and rejects a correct program (VER103)
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
