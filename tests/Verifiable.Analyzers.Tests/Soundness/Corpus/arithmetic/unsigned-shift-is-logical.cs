// A shift of a uint is logical: the high bit moves down as a zero, so (uint.MaxValue >> 1) is 2^31 - 1, not uint.MaxValue.
// @expect: rejected
// @witness: Corpus.Arithmetic.LogicalShift.Half(4294967295)
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class LogicalShift
{
    public static uint Half(uint x)
    {
        Spec.Ensures<uint>(r => r >= x / 2u + 1u || x < 2u, "At least half, rounded up.");
        return x >> 1;
    }
}
