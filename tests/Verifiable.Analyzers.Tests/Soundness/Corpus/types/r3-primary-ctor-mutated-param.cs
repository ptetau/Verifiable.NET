// A primary-constructor parameter that a member mutates is not a plain stored value: the struct must not be lowered as a projection.
// @expect: rejected
// @witness: Corpus.Types.R3Bump.Go(5)
using Verifiable.Contracts;

namespace Corpus.Types;

[Verifiable]
public struct R3Counter(int n)
{
    public int Bump() { n++; return n; }
}

[Verifiable]
public static class R3Bump
{
    public static int Go(int x)
    {
        Spec.Requires(x >= 0 && x <= 1000, "Small.");
        Spec.Ensures<int>(r => r == x, "Wrongly claims no change.");
        var c = new R3Counter(x);
        return c.Bump();
    }
}
