// For bool?, null & false is false (not null).
// @expect: rejected
// @witness: Corpus.Strings.TriBool.StillNull(null)
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class TriBool
{
    public static bool StillNull(bool? a)
    {
        Spec.Requires(a == null, "Unknown.");
        Spec.Ensures<bool>(r => r, "Claims unknown & false is unknown.");
        return (a & false) == null;
    }
}
