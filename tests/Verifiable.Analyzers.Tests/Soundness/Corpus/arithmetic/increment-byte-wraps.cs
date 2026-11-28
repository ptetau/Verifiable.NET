// b++ on a byte wraps 255 to 0.
// @expect: rejected
// @witness: Corpus.Arithmetic.ByteIncrement.Bump(255)
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class ByteIncrement
{
    public static int Bump(byte x)
    {
        Spec.Ensures<int>(r => r > x, "The bumped value is bigger.");
        byte b = x;
        b++;
        return b;
    }
}
