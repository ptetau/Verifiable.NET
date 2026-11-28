// x.Value on an empty int? throws InvalidOperationException; nothing guards it.
// @expect: rejected
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
