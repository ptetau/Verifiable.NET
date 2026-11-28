// A with-expression can move Lo past Hi; returning the result must check Span's [Invariant].
// @expect: rejected
// @witness: Corpus.Types.SpanWith.Shift(new Corpus.Types.Span(0, 1), 5)
using Verifiable.Contracts;

namespace Corpus.Types;

[Verifiable]
public sealed record Span(int Lo, int Hi)
{
    [Invariant("Lo never exceeds Hi.")]
    public bool Ordered() => Lo <= Hi;
}

[Verifiable]
public static class SpanWith
{
    public static Span Shift(Span s, int by)
    {
        Spec.Requires(by >= 0, "Shift forwards.");
        Spec.Ensures<Span>(r => r.Lo <= r.Hi, "Still ordered.");
        return s with { Lo = s.Lo + by };
    }
}
