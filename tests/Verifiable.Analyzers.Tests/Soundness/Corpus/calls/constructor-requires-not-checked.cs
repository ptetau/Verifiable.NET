// A constructor's Spec.Requires is a precondition callers must meet; here the caller passes a negative value.
// @expect: rejected
// @witness: Corpus.Calls.CtorRequires.Make(-1)
// @known-bug: Calls to a constructor never check its Spec.Requires (LowerObjectCreation does not call CheckMethodRequires).
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public readonly record struct Age
{
    public Age(int years)
    {
        Spec.Requires(years >= 0, "Ages are never negative.");
        Years = years;
    }

    public int Years { get; }
}

[Verifiable]
public static class CtorRequires
{
    public static int Make(int x)
    {
        Spec.Requires(x >= -5 && x <= 5, "Small inputs only.");
        var a = new Age(x);
        return a.Years;
    }
}
