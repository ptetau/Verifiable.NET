// Generic method with T : struct and a correct contract, called with two instantiations.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public static class R2GenOk
{
    public static int Const<T>(T x, int k) where T : struct
    {
        Spec.Requires(k >= 0 && k < 100, "Small.");
        Spec.Ensures<int>(r => r == k + 1, "Returns k + 1.");
        return k + 1;
    }

    public static int Use(int a, bool b)
    {
        Spec.Requires(a >= 0 && a < 99, "Small enough that a + 1 is still below 100.");
        Spec.Ensures<int>(r => r == a + 2, "Adds two.");
        return Const(b, Const(a, a));
    }
}
