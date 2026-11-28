// A Derived record is never == a Base record (EqualityContract differs), even when the Base parts match.
// Today it is rejected only because Z3 reports a sort clash between the two record datatypes (VER103).
// @expect: rejected
// @witness: Corpus.Types.RecordInheritance.Same(1)
using Verifiable.Contracts;

namespace Corpus.Types;

public record Base(int X);
public record Derived(int X, int Y) : Base(X);

[Verifiable]
public static class RecordInheritance
{
    public static bool Same(int x)
    {
        Spec.Ensures<bool>(r => r == true, "Same X means equal.");
        Base b = new Derived(x, 0);
        return b == new Base(x);
    }
}
