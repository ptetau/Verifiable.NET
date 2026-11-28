// (int) of a decimal truncates towards zero, so -2.9m becomes -2, not -3.
// @expect: rejected
// @witness: Corpus.Arithmetic.R2DecTrunc.Run(-2.9m)
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class R2DecTrunc
{
    public static int Run(decimal d)
    {
        Spec.Requires(d == -2.9m, "d is -2.9.");
        Spec.Ensures<int>(r => r == -3, "Floored.");
        return (int)d;
    }
}
