// Generic method with T : struct whose contract is false for the instantiation used.
// @expect: rejected
// @witness: Corpus.Calls.R2Gen.Use(5)
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public static class R2Gen
{
    public static int Const<T>(T x, int k) where T : struct
    {
        Spec.Requires(k >= 0 && k < 100, "Small.");
        Spec.Ensures<int>(r => r == k, "Returns k.");
        return k + 1;
    }

    public static int Use(int a)
    {
        Spec.Requires(a >= 0 && a < 100, "Small.");
        Spec.Ensures<int>(r => r == a, "Returns a.");
        return Const(a, a);
    }
}
