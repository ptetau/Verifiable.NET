// checked((byte)x) throws OverflowException for x = 256, so the analyzer must flag the cast.
// @expect: rejected
// @witness: Corpus.Arithmetic.CheckedByteCast.Narrow(256)
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class CheckedByteCast
{
    public static byte Narrow(int x)
    {
        Spec.Requires(x >= 0 && x <= 300, "A small input.");
        Spec.Ensures<byte>(r => r == x, "The value is kept.");
        return checked((byte)x);
    }
}
