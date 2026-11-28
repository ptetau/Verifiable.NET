// `new` hides Base.F with a method that has no contract and returns -1; a call on the derived static type must not use Base.F's promise.
// @expect: rejected
// @witness: Corpus.Calls.R2Hide.Use(new Corpus.Calls.R2HideDerived(1))
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public record R2HideBase(int A)
{
    public int F()
    {
        Spec.Ensures<int>(r => r >= 0, "Non-negative.");
        return 1;
    }
}

[Verifiable]
public sealed record R2HideDerived(int A) : R2HideBase(A)
{
    public new int F() => -1;
}

[Verifiable]
public static class R2Hide
{
    public static int Use(R2HideDerived d)
    {
        Spec.Ensures<int>(r => r >= 0, "Relies on the hidden base promise.");
        return d.F();
    }
}
