// A class primary-constructor parameter is a mutable captured variable; Next mutates it.
// @expect: rejected
// @witness: Corpus.Types.PrimCap.Twice(1)
using Verifiable.Contracts;

namespace Corpus.Types;

[Verifiable]
public sealed class Counter2(int n)
{
    public int Next()
    {
        n++;
        return n;
    }
}

[Verifiable]
public static class PrimCap
{
    public static int Twice(int x)
    {
        Spec.Requires(x >= 0 && x <= 1000, "Small.");
        Spec.Ensures<int>(r => r == x + 1, "One step.");
        var c = new Counter2(x);
        c.Next();
        return c.Next();
    }
}
