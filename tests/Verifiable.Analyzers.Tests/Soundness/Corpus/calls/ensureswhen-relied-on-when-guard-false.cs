// The callee only promises a positive result when its flag is set; the caller clears the flag and still relies on it.
// @expect: rejected
// @witness: Corpus.Calls.EnsuresWhenCaller.Use(0)
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public static class EnsuresWhenCaller
{
    public static int Pick(int x, bool positive)
    {
        Spec.Requires(x >= -100 && x <= 100, "Small inputs only.");
        Spec.EnsuresWhen<int>(positive, r => r > 0, "Positive when asked.");
        return positive ? 1 : x;
    }

    public static int Use(int x)
    {
        Spec.Requires(x >= -100 && x <= 100, "Small inputs only.");
        Spec.Ensures<int>(r => r > 0, "Wrongly relies on the guarded promise.");
        return Pick(x, false);
    }
}
