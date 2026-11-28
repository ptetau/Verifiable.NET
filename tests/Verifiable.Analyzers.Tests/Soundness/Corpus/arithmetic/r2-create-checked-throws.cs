// int.CreateChecked(long) throws OverflowException when the value does not fit, so r == x does not hold for every long.
// @expect: rejected
// @witness: Corpus.Arithmetic.R2CreateChecked.Run(4294967296L)
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class R2CreateChecked
{
    public static int Run(long x)
    {
        Spec.Ensures<int>(r => r == x, "Same value.");
        return int.CreateChecked(x);
    }
}
