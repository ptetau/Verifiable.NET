// Tuple == uses the element ==, and NaN == NaN is false.
// @expect: rejected
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
