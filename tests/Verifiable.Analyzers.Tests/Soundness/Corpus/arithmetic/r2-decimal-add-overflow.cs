// decimal.MaxValue + 1 throws OverflowException, so the sum is not always above d.
// @expect: rejected
// @witness: Corpus.Arithmetic.R2DecOverflow.Run(79228162514264337593543950335m)
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class R2DecOverflow
{
    public static decimal Run(decimal d)
    {
        Spec.Requires(d >= 0m, "Not negative.");
        Spec.Ensures<decimal>(r => r > d, "Bigger.");
        return d + 1m;
    }
}
