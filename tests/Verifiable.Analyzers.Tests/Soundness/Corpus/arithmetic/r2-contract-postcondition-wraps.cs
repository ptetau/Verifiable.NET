// In the contract r + 1 is mathematical, but at run time r + 1 wraps for r == int.MaxValue so r + 1 > r fails.
// @expect: rejected
// @witness: Corpus.Arithmetic.R2PostWraps.Run(int.MaxValue)
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class R2PostWraps
{
    public static int Run(int x)
    {
        Spec.Ensures<int>(r => r + 1 > r, "The successor of the result is bigger.");
        return x;
    }
}
