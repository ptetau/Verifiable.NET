// For an odd negative x, x % 2 is -1 in C#, not 1.
// @expect: rejected
// @witness: Corpus.Arithmetic.OddRemainder.Parity(-1)
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
[CheckedArithmetic]
public static class OddRemainder
{
    public static int Parity(int x)
    {
        Spec.Requires(x % 2 != 0, "x is odd.");
        Spec.Ensures<int>(r => r == 1, "The remainder is 1.");
        return x % 2;
    }
}
