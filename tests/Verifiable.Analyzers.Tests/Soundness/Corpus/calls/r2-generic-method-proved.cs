// Generic method with T : struct and a correct contract, called with two instantiations.
// @expect: proved
// @known-bug: calling a generic method (T : struct) with a Requires on another parameter makes the SMT query reference an undeclared constant (VER103 unknown constant p_k)
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
        Spec.Requires(a >= 0 && a < 100, "Small.");
        Spec.Ensures<int>(r => r == a + 2, "Adds two.");
        return Const(b, Const(a, a));
    }
}
