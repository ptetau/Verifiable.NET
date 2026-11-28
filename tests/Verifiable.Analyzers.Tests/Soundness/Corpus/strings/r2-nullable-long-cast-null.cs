// (long)x on a null int? throws InvalidOperationException.
// @expect: rejected
// @witness: Corpus.Strings.CastNull.Widen(null)
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class CastNull
{
    public static long Widen(int? x)
    {
        Spec.Ensures<long>(r => r >= int.MinValue, "Fits.");
        return (long)x;
    }
}
