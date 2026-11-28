// An explicit checked(...) still throws inside an [UncheckedArithmetic] scope, so int.MaxValue + 1 is not returned.
// @expect: rejected
// @witness: Corpus.Arithmetic.CheckedInside.Next(int.MaxValue)
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
[UncheckedArithmetic]
public static class CheckedInside
{
    public static int Next(int x)
    {
        Spec.Ensures<int>(r => r > x, "The successor is bigger.");
        return checked(x + 1);
    }
}
