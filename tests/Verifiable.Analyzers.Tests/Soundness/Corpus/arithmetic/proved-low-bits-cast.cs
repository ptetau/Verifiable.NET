// A correct program: a cast to ushort or sbyte keeps the low bits, so the result is always in the type's range.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class LowBitsCast
{
    public static int Low16(int x)
    {
        Spec.Ensures<int>(r => r >= 0 && r <= 65535, "A ushort.");
        return (ushort)x;
    }

    public static int Signed8(int x)
    {
        Spec.Ensures<int>(r => r >= -128 && r <= 127, "An sbyte.");
        return (sbyte)x;
    }

    public static uint Reinterpret(int x)
    {
        Spec.Requires(x < 0, "Negative.");
        Spec.Ensures<uint>(r => r >= 2147483648u, "The sign bit becomes a high bit.");
        return (uint)x;
    }
}
