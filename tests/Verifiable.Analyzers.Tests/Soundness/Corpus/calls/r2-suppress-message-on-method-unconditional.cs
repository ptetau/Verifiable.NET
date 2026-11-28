// [UnconditionalSuppressMessage] on the method hides VER102.
// @expect: rejected
// @witness: Corpus.Calls.R2SupUncond.Next(3)
using System.Diagnostics.CodeAnalysis;
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public static class R2SupUncond
{
    [UnconditionalSuppressMessage("Verifiable", "VER102", Justification = "trust me")]
    public static int Next(int x)
    {
        Spec.Requires(x >= 0 && x <= 1000, "Small.");
        Spec.Ensures<int>(r => r > x, "Claims to grow.");
        return x;
    }
}
