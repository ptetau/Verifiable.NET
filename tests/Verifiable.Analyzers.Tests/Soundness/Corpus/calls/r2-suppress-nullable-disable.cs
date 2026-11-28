// `#nullable disable` around the method must not change the verdict on a false postcondition (nullability suppressor interplay).
// @expect: rejected
// @witness: Corpus.Calls.R2Nullable.Next("", 3)
#nullable enable
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public static class R2Nullable
{
#nullable disable
    public static int Next(string s, int x)
    {
        Spec.Requires(x >= 0 && x <= 1000, "Small.");
        Spec.Ensures<int>(r => r > x, "Claims to grow.");
        return x;
    }
#nullable restore
}
