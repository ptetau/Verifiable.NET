// Tuple != is element-wise: with a NaN element, (d, 1) != (d, 1) is true.
// @expect: rejected
// @witness: Corpus.Types.TupNanNe.Same(double.NaN)
using Verifiable.Contracts;

namespace Corpus.Types;

[Verifiable]
public static class TupNanNe
{
    public static bool Same(double d)
    {
        Spec.Ensures<bool>(r => r == false, "A tuple never differs from itself.");
        return (d, 1) != (d, 1);
    }
}
