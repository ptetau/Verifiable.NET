// int.CreateTruncating(long) keeps the low 32 bits, so the result need not equal the long.
// @expect: rejected
// @witness: Corpus.Arithmetic.R2CreateTrunc.Run(4294967297L)
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class R2CreateTrunc
{
    public static int Run(long x)
    {
        Spec.Ensures<int>(r => r == x, "Same value.");
        return int.CreateTruncating(x);
    }
}
