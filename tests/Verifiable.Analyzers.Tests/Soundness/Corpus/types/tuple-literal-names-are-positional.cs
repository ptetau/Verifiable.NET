// Tuple literal names that differ from the target's are ignored (CS8123): (Hi: y, Lo: x) puts y in Lo.
// @expect: rejected
// @witness: Corpus.Types.TupleNames.Make(1, 2)
using Verifiable.Contracts;

namespace Corpus.Types;

[Verifiable]
public static class TupleNames
{
    public static (int Lo, int Hi) Make(int x, int y)
    {
        Spec.Requires(x != y, "Distinct values.");
        Spec.Ensures<(int Lo, int Hi)>(r => r.Lo == x, "Lo is x.");
        return (Hi: y, Lo: x);
    }
}
