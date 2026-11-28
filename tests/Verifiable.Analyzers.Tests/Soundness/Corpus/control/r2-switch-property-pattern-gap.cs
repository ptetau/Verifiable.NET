// {X: > 0, Y: > 0} and {X: <= 0} leave out (1, 0): the switch throws.
// @expect: rejected
// @witness: Corpus.Control.PropGap.Cls(new Corpus.Control.PropPoint(1, 0))
using Verifiable.Contracts;

namespace Corpus.Control;

public readonly record struct PropPoint(int X, int Y);

[Verifiable]
public static class PropGap
{
    public static int Cls(PropPoint p)
    {
        Spec.Ensures<int>(r => r == 1 || r == 2, "A class.");
#pragma warning disable CS8509
        return p switch
        {
            { X: > 0, Y: > 0 } => 1,
            { X: <= 0 } => 2,
        };
#pragma warning restore CS8509
    }
}
