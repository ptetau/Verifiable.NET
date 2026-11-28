// A correct program: under [UncheckedArithmetic] the successor wraps at the top, and the contract says so.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
[UncheckedArithmetic]
public static class WrapOk
{
    public static int Next(int x)
    {
        Spec.Ensures<int>(r => (x == int.MaxValue && r == int.MinValue) || r == x + 1, "Wraps at the top.");
        return x + 1;
    }

    public static uint Prev(uint x)
    {
        Spec.Ensures<uint>(r => (x == 0u && r == uint.MaxValue) || r == x - 1u, "Wraps at zero.");
        return x - 1u;
    }
}
