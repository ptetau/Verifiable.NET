// (int)d truncates toward zero in C#, so (int)-0.5m is 0, not -1 (floor).
// @expect: rejected
// @witness: Corpus.Arithmetic.DecimalTrunc.Whole(-0.5m)
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class DecimalTrunc
{
    public static int Whole(decimal d)
    {
        Spec.Requires(d > -1m && d < 0m, "Strictly between -1 and 0.");
        Spec.Ensures<int>(r => r == -1, "Rounds down to -1.");
        return (int)d;
    }
}
