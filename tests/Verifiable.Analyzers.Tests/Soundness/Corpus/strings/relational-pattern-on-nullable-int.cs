// Correct: `x is > 5` on an int? is true exactly when x has a value above 5.
// @expect: proved
// @known-bug: false rejection: a relational pattern on an int? compares the Opt term with an Int, so Z3 reports a sort error (VER103)
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
