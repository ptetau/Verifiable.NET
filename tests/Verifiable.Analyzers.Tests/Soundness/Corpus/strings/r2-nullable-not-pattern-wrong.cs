// `x is not > 3` is true for null, so it does not imply a value is present.
// @expect: rejected
// @witness: Corpus.Strings.NotPattern.Small(null)
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class NotPattern
{
    public static bool Small(int? x)
    {
        Spec.Ensures<bool>(r => !r || x.HasValue, "Claims a match has a value.");
        return x is not > 3;
    }
}
