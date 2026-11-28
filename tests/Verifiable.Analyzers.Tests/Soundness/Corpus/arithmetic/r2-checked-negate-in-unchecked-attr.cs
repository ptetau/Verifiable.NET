// -x inside checked(...) throws for int.MinValue even in an [UncheckedArithmetic] method.
// @expect: rejected
// @witness: Corpus.Arithmetic.R2CheckedNeg.Run(int.MinValue)
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
[UncheckedArithmetic]
public static class R2CheckedNeg
{
    public static int Run(int x)
    {
        Spec.Ensures<int>(r => r == x || r == -x, "Negated, or int.MinValue is its own negation.");
        return checked(-x);
    }
}
