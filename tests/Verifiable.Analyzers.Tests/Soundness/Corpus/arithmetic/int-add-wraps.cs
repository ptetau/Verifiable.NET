// C# int addition wraps by default, so x + 1 is not always bigger than x. The prover now checks int overflow by default.
// @expect: rejected
// @witness: Corpus.Arithmetic.IntWrap.Next(int.MaxValue)
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class IntWrap
{
    public static int Next(int x)
    {
        Spec.Requires(x >= 0, "Non-negative input.");
        Spec.Ensures<int>(r => r > x, "Result is bigger than the input.");
        return x + 1;
    }
}
