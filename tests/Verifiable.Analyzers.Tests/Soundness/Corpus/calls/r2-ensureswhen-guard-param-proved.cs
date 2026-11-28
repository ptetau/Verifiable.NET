// EnsuresWhen on a parameter guard is used by the caller only when the guard holds (correct).
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public static class R2EWOk
{
    public static int Pick(int x, bool big)
    {
        Spec.Requires(x >= 0 && x <= 100, "Small.");
        Spec.EnsuresWhen<int>(big, r => r >= 100, "Big gives at least 100.");
        return big ? x + 100 : x;
    }

    public static int Use(int x)
    {
        Spec.Requires(x >= 0 && x <= 100, "Small.");
        Spec.Ensures<int>(r => r >= 100, "At least 100.");
        return Pick(x, true);
    }
}
