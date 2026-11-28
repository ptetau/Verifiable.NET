// Explicit interface implementation with a false postcondition.
// @expect: rejected
// @witness: Corpus.Calls.R2Expl.Use(5)
using Verifiable.Contracts;

namespace Corpus.Calls;

public interface IR2Expl { int Twice(int x); }

[Verifiable]
public sealed record R2ExplImpl(int A) : IR2Expl
{
    int IR2Expl.Twice(int x)
    {
        Spec.Requires(x >= 0 && x <= 100, "Small.");
        Spec.Ensures<int>(r => r == x * 2, "Doubles.");
        return x * 3;
    }
}

[Verifiable]
public static class R2Expl
{
    public static int Use(int x)
    {
        Spec.Requires(x >= 0 && x <= 100, "Small.");
        Spec.Ensures<int>(r => r == x * 2, "Doubles.");
        return ((IR2Expl)new R2ExplImpl(0)).Twice(x);
    }
}
