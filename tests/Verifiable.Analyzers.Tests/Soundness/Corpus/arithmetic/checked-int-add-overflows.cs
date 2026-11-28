// With [CheckedArithmetic], x + 1 can overflow, so the analyzer must flag it.
// @expect: rejected
// @witness: Corpus.Arithmetic.CheckedWrap.Next(int.MaxValue)
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
[CheckedArithmetic]
public static class CheckedWrap
{
    public static int Next(int x)
    {
        Spec.Requires(x >= 0, "Non-negative input.");
        Spec.Ensures<int>(r => r > x, "Result is bigger than the input.");
        return checked(x + 1);
    }
}
