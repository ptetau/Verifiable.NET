// Every (a, b) falls in exactly one quadrant arm, so the switch never throws.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class TupleExhaustive
{
    public static int Cls(int a, int b)
    {
        Spec.Ensures<int>(r => r >= 1 && r <= 4, "A quadrant class.");
        return (a, b) switch
        {
            (> 0, > 0) => 1,
            (<= 0, > 0) => 2,
            (> 0, <= 0) => 3,
            (<= 0, <= 0) => 4,
        };
    }
}
