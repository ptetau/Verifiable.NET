// The constructor reads A before assigning it, so B is 1, not a + 1.
// @expect: rejected
// @witness: Corpus.Types.CtorRead.Make(5)
using Verifiable.Contracts;

namespace Corpus.Types;

[Verifiable]
public readonly struct Acc
{
    public int A { get; }
    public int B { get; }
    public Acc(int a)
    {
        B = A + 1;
        A = a;
    }
}

[Verifiable]
public static class CtorRead
{
    public static int Make(int a)
    {
        Spec.Requires(a >= 0 && a <= 1000, "Small.");
        Spec.Ensures<int>(r => r == a + 1, "B is a + 1.");
        return new Acc(a).B;
    }
}
