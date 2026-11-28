// x % 4 equals x & 3 only for non-negative x; for -1 it is -1 versus 3.
// @expect: rejected
// @witness: Corpus.Arithmetic.R2RemMask.Run(-1)
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class R2RemMask
{
    public static int Run(int x)
    {
        Spec.Ensures<int>(r => r == (x & 3), "Remainder equals mask.");
        return x % 4;
    }
}
