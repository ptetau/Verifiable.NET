// A correct program: a uint shift by 33 is a shift by 1.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class R2UintShift
{
    public static uint Run(uint x)
    {
        Spec.Ensures<uint>(r => r == x / 2u, "Halved.");
        return x >> 33;
    }
}
