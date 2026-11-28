// Correct: `x is > 5` on an int? is true exactly when x has a value above 5.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class NullableRelationalOk
{
    public static bool Big(int? x)
    {
        Spec.Ensures<bool>(r => r == (x != null && x.Value > 5), "Above five.");
        return x is > 5;
    }
}
