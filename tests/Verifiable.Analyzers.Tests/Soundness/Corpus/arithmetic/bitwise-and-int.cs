// A correct program: x & 1 is always 0 or 1.
// @expect: proved
// @known-bug: False rejection (VER103): integer & is lowered as the Boolean (and ...), a Z3 sort error.
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
