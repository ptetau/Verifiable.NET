// The compiler folds unchecked(int.MaxValue + 1) to int.MinValue, which is not positive.
// @expect: rejected
// @witness: Corpus.Arithmetic.R2ConstFold.Run(0)
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class R2ConstFold
{
    private const int Top = int.MaxValue;

    public static int Run(int x)
    {
        Spec.Ensures<int>(r => r > 0, "Positive.");
        return unchecked(Top + 1);
    }
}
