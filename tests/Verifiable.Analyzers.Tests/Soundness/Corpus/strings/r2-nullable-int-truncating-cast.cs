// (int?)l wraps for a long outside int range, so the value is not preserved.
// @expect: rejected
// @witness: Corpus.Strings.TruncNullable.Narrow(4294967297L)
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class TruncNullable
{
    public static int? Narrow(long l)
    {
        Spec.Ensures<int?>(r => r.HasValue && r.Value == l, "Claims the value is kept.");
        return unchecked((int?)l);
    }
}
