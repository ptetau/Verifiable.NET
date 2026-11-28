// A correct program: a byte shifted left is an int, and the cast back to byte keeps the low eight bits.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class R2ByteShift
{
    public static byte Run(byte b)
    {
        Spec.Ensures<byte>(r => r == (b * 2) % 256, "Doubled modulo 256.");
        return (byte)(b << 1);
    }
}
