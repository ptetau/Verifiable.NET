// D divides by C - 2 through a chain of computed properties; A = 0 makes C = 2.
// @expect: rejected
// @witness: Corpus.Types.NestedComp.Get(0)
using Verifiable.Contracts;

namespace Corpus.Types;

[Verifiable]
public sealed record Chain(int A)
{
    public int B => A + 1;
    public int C => B * 2;
    public int D => Quot(C);
    private static int Quot(int c) => 100 / (c - 2);
}

[Verifiable]
public static class NestedComp
{
    public static int Get(int a)
    {
        Spec.Requires(a >= 0 && a <= 100, "Small.");
        Spec.Ensures<int>(r => r >= 0, "Non-negative.");
        return new Chain(a).D;
    }
}
