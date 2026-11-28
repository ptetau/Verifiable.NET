// A positional pattern on a record needs its Deconstruct method, which is not modelled: rejected, never assumed to match.
// @expect: rejected
// @witness: Corpus.Control.RecordGap.Cls(new Corpus.Control.Pt(0, 1))
using Verifiable.Contracts;

namespace Corpus.Control;

public readonly record struct Pt(int X, int Y);

[Verifiable]
public static class RecordGap
{
    public static int Cls(Pt p)
    {
        Spec.Ensures<int>(r => r == 1 || r == 2, "A class.");
#pragma warning disable CS8509
        return p switch
        {
            (> 0, _) => 1,
            (< 0, _) => 2,
        };
#pragma warning restore CS8509
    }
}
