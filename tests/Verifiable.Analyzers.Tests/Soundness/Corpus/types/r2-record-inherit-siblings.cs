// Sibling records with the same Base part have different EqualityContract, so they are not equal.
// @expect: rejected
// @witness: Corpus.Types.InhSib.Same(1)
using Verifiable.Contracts;

namespace Corpus.Types;

public record SBase { public int X { get; init; } }
public record SLeft : SBase;
public record SRight : SBase;

[Verifiable]
public static class InhSib
{
    public static bool Same(int x)
    {
        Spec.Ensures<bool>(r => r == true, "Same X means equal.");
        SBase a = new SLeft { X = x };
        SBase b = new SRight { X = x };
        return a == b;
    }
}
