// The computed property divides by B, which may be 0; reading it through the property must still check the divisor.
// @expect: rejected
// @witness: Corpus.Types.ComputedProperty.Get(new Corpus.Types.Ratio(1, 0))
// @known-bug: A computed property read is inlined with emitObligations: false, so the division-by-zero check inside it is dropped (ResolveMemberAccess in CfgSsaLowerer.cs).
using Verifiable.Contracts;

namespace Corpus.Types;

public readonly record struct Ratio(int A, int B)
{
    public int Quotient => A / B;
}

[Verifiable]
public static class ComputedProperty
{
    public static int Get(Ratio r)
    {
        Spec.Ensures<int>(x => true, "Returns some quotient.");
        return r.Quotient;
    }
}
