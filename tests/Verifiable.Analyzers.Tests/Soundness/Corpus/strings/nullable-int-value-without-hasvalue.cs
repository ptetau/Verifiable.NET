// x.Value on an empty int? throws InvalidOperationException; nothing guards it.
// @expect: rejected
// @known-bug: x.Value on an int? has no HasValue obligation; Value of an empty int? is an unconstrained opt-val
// @witness: Corpus.Strings.NullableValue.Unwrap(null)
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class NullableValue
{
    public static int Unwrap(int? x)
    {
        Spec.Ensures<int>(r => r == x.Value, "Returns the payload.");
        return x.Value;
    }
}
