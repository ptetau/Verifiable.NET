// Lifted + wraps at int.MaxValue, so x + 1 > x does not hold for every non-null x.
// @expect: rejected
// @witness: Corpus.Strings.LiftedAdd.Bigger(int.MaxValue)
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class LiftedAdd
{
    public static bool Bigger(int? x)
    {
        Spec.Requires(x.HasValue, "Has a value.");
        Spec.Ensures<bool>(r => r, "Claims x + 1 > x.");
        return x + 1 > x;
    }
}
