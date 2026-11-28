// The base method promises a non-negative area; an override returns -1. A caller through the base type relies on
// the base promise, so the override must be held to it (behavioural subtyping).
// @expect: rejected
// @witness: Corpus.Calls.VirtualCalls.AreaOf(new Corpus.Calls.NegShape(1))
// @known-bug: A virtual call assumes the base method's Spec.Ensures, but overrides are never checked against it.
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public record Shape(int W)
{
    public virtual int Area()
    {
        Spec.Ensures(Spec.Result<int>() >= 0, "Areas are never negative.");
        return 0;
    }
}

[Verifiable]
public sealed record NegShape(int W) : Shape(W)
{
    public override int Area() => -1;
}

[Verifiable]
public static class VirtualCalls
{
    public static int AreaOf(Shape s)
    {
        Spec.Ensures<int>(r => r >= 0, "Relies on the base promise.");
        return s.Area();
    }
}
