// Two Derived records with equal Base part but different Y, compared through the Base type, are not equal.
// @expect: rejected
// @witness: Corpus.Types.InhStatic.Same(1)
using Verifiable.Contracts;

namespace Corpus.Types;

public record IBase { public int X { get; init; } }
public record IDerived : IBase { public int Y { get; init; } }

[Verifiable]
public static class InhStatic
{
    public static bool Same(int x)
    {
        Spec.Ensures<bool>(r => r == true, "Same X means equal.");
        IBase a = new IDerived { X = x, Y = 1 };
        IBase b = new IDerived { X = x, Y = 2 };
        return a == b;
    }
}
