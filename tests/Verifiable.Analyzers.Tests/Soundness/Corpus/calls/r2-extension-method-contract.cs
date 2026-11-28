// Extension method with a false postcondition used via extension syntax.
// @expect: rejected
// @witness: Corpus.Calls.R2Ext.Use(3)
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public static class R2Ext
{
    public static int Inc(this int x)
    {
        Spec.Requires(x >= 0 && x < 100, "Small.");
        Spec.Ensures<int>(r => r == x + 1, "Increments.");
        return x + 2;
    }

    public static int Use(int a)
    {
        Spec.Requires(a >= 0 && a < 100, "Small.");
        Spec.Ensures<int>(r => r == a + 1, "Increments.");
        return a.Inc();
    }
}
