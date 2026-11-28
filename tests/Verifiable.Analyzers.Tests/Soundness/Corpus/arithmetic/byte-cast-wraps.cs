// (byte) keeps only the low 8 bits, so (byte)256 is 0, not 256.
// @expect: rejected
// @witness: Corpus.Arithmetic.ByteWrap.Roundtrip(256)
// @known-bug: Unchecked (byte) casts are not wrapped to 8 bits (TryLowerIntegralOrEnumConversion returns false when not checked, ConvertTo is the identity).
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class ByteWrap
{
    public static int Roundtrip(int x)
    {
        Spec.Requires(x >= 0, "Non-negative input.");
        Spec.Ensures<int>(r => r == x, "Converting to byte and back changes nothing.");
        return (byte)x;
    }
}
