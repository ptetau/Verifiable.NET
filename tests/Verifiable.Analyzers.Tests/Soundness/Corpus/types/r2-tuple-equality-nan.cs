// Tuple == uses the element ==, and NaN == NaN is false.
// @expect: rejected
// @known-bug: tuple == on doubles is modelled as reflexive on NaN, so (d, 1) == (d, 1) is proved true although NaN == NaN is false (a false proof)
// @witness: Corpus.Types.TupNan.Same(double.NaN)
using Verifiable.Contracts;

namespace Corpus.Types;

[Verifiable]
public static class TupNan
{
    public static bool Same(double d)
    {
        Spec.Ensures<bool>(r => r == true, "A tuple equals itself.");
        return (d, 1) == (d, 1);
    }
}
