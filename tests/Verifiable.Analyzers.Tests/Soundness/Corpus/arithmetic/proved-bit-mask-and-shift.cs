// A correct program: shifting and masking leave a value in 0..255, whatever the sign of the input.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class BitMaskShift
{
    public static int SecondByte(int x)
    {
        Spec.Ensures<int>(r => r >= 0 && r <= 255, "A byte.");
        return (x >> 8) & 0xFF;
    }

    public static uint TopBit(uint x)
    {
        Spec.Ensures<uint>(r => r <= 1u, "A single bit.");
        return x >> 31;
    }
}
