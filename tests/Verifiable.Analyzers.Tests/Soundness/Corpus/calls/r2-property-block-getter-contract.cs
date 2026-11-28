// A block-bodied property getter with a false postcondition.
// @expect: rejected
// @witness: Corpus.Calls.R2PropGet.Use(new Corpus.Calls.R2Box(3))
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public sealed record R2Box(int V)
{
    public int Doubled
    {
        get
        {
            Spec.Requires(V >= 0 && V <= 100, "Small.");
            Spec.Ensures<int>(r => r == V * 2, "Doubles.");
            return V * 3;
        }
    }
}

[Verifiable]
public static class R2PropGet
{
    public static int Use(R2Box b)
    {
        Spec.Requires(b.V >= 0 && b.V <= 100, "Small.");
        Spec.Ensures<int>(r => r == b.V * 2, "Doubles.");
        return b.Doubled;
    }
}
