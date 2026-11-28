// int.Abs (generic math) throws on int.MinValue just like Math.Abs; its result must not be assumed to be in range.
// @expect: rejected
// @witness: Corpus.Arithmetic.R2GenericAbs.Run(int.MinValue)
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class R2GenericAbs
{
    public static int Run(int x)
    {
        Spec.Ensures<int>(r => r >= 0, "The absolute value is not negative.");
        return int.Abs(x);
    }
}
