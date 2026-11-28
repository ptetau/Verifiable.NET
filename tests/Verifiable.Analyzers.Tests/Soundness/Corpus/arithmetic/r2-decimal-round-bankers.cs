// Math.Round on decimal rounds half to even, so 2.5m becomes 2, not 3.
// @expect: rejected
// @witness: Corpus.Arithmetic.R2DecRound.Run(2.5m)
using System;
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class R2DecRound
{
    public static decimal Run(decimal d)
    {
        Spec.Requires(d == 2.5m, "d is 2.5.");
        Spec.Ensures<decimal>(r => r == 3m, "Rounded up.");
        return Math.Round(d);
    }
}
