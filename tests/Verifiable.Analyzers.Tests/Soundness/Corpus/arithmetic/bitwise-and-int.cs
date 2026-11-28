// A correct program: x & 1 is always 0 or 1.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class BitwiseAnd
{
    public static int LowBit(int x)
    {
        Spec.Ensures<int>(r => r >= 0 && r <= 1, "The low bit is 0 or 1.");
        return x & 1;
    }
}
