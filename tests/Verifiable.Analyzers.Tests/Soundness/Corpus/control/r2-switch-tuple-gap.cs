// (0, 5) matches none of the three tuple arms and throws.
// @expect: rejected
// @witness: Corpus.Control.TupleGap.Cls(0, 5)
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class TupleGap
{
    public static int Cls(int a, int b)
    {
        Spec.Ensures<int>(r => r >= 1 && r <= 3, "A class.");
#pragma warning disable CS8509
        return (a, b) switch
        {
            (> 0, > 0) => 1,
            (< 0, _) => 2,
            (_, < 0) => 3,
        };
#pragma warning restore CS8509
    }
}
