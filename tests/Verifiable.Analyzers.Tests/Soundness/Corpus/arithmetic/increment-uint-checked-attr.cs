// A correct program: for x below 100, incrementing a uint copy gives x + 1.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
[CheckedArithmetic]
public static class UintIncrement
{
    public static uint Bump(uint x)
    {
        Spec.Requires(x < 100u, "Small input.");
        Spec.Ensures<uint>(r => r == x + 1u, "Adds one.");
        uint u = x;
        u++;
        return u;
    }
}
