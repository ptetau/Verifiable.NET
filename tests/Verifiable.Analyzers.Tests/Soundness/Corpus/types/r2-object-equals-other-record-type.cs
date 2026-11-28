// Equals(object) on records of different types with the same shape is false.
// @expect: rejected
// @witness: Corpus.Types.ObjEq.Same(1)
using Verifiable.Contracts;

namespace Corpus.Types;

public sealed record RecA(int X);
public sealed record RecB(int X);

[Verifiable]
public static class ObjEq
{
    public static bool Same(int x)
    {
        Spec.Ensures<bool>(r => r == true, "Same shape means equal.");
        object a = new RecA(x);
        return a.Equals(new RecB(x));
    }
}
