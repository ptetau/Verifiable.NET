// Lifted comparisons on int? are false when either side is null, so `a < b || a >= b` is not always true.
// @expect: rejected
// @witness: Corpus.Strings.LiftedCompare.Total(null, 1)
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class LiftedCompare
{
    public static bool Total(int? a, int? b)
    {
        Spec.Ensures<bool>(r => r, "Claims int? comparison is total.");
        return a < b || a >= b;
    }
}
