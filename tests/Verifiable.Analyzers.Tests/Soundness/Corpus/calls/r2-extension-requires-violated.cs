// Extension method with a precondition; the caller via extension syntax violates it.
// @expect: rejected
// @witness: Corpus.Calls.R2ExtReq.Use(-5)
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public static class R2ExtReq
{
    public static int Sqrtish(this int x)
    {
        Spec.Requires(x >= 0, "Non-negative.");
        Spec.Ensures<int>(r => r >= 0, "Non-negative.");
        return x / 2;
    }

    public static int Use(int a)
    {
        Spec.Requires(a >= -10 && a <= 10, "Small.");
        return a.Sqrtish();
    }
}
