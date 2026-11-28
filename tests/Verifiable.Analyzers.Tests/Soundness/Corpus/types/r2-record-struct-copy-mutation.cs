// record struct is mutable: the copy is changed, the original is not, so they differ.
// @expect: rejected
// @witness: Corpus.Types.RsCopy.Same(1)
using Verifiable.Contracts;

namespace Corpus.Types;

public record struct MPoint(int X);

[Verifiable]
public static class RsCopy
{
    public static bool Same(int x)
    {
        Spec.Requires(x >= 0 && x <= 1000, "Small.");
        Spec.Ensures<bool>(r => r == true, "A copy equals its original.");
        var a = new MPoint(x);
        var b = a;
        b.X = x + 1;
        return a == b;
    }
}
