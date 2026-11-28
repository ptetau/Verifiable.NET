// b += 1 on a byte stores (byte)(b + 1), so 255 becomes 0, not 256.
// @expect: rejected
// @witness: Corpus.Arithmetic.ByteCompound.Bump(255)
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class ByteCompound
{
    public static int Bump(byte x)
    {
        Spec.Ensures<int>(r => r > x, "The bumped value is bigger.");
        byte b = x;
        b += 1;
        return b;
    }
}
