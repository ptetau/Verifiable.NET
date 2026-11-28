// `x is not (1 or 2)` is true for null.
// @expect: rejected
// @witness: Corpus.Strings.NotOr.Other(null)
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class NotOr
{
    public static bool Other(int? x)
    {
        Spec.Ensures<bool>(r => !r || (x.HasValue && x.Value != 1 && x.Value != 2), "Claims a value that is neither.");
        return x is not (1 or 2);
    }
}
