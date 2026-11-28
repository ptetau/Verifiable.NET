// C# long multiplication wraps by default, so x * 2 is not always bigger than x for a positive x.
// @expect: rejected
// @witness: Corpus.Arithmetic.LongMul.Double(long.MaxValue)
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class LongMul
{
    public static long Double(long x)
    {
        Spec.Requires(x > 0L, "Positive input.");
        Spec.Ensures<long>(r => r > x, "The double is bigger than the input.");
        return x * 2L;
    }
}
