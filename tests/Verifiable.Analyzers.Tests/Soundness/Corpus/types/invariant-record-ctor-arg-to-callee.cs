// new Span(5, 1) breaks Span's [Invariant] Lo <= Hi, yet it is passed to a callee that relies on it.
// @expect: rejected
// @witness: Corpus.Types.SpanArg.Caller(5, 1)
// @known-bug: Call sites never check a callee parameter's [Invariant], so new Span(5, 1) is passed to a callee that assumes Lo <= Hi.
using Verifiable.Contracts;

namespace Corpus.Types;

[Verifiable]
public sealed record Span(int Lo, int Hi)
{
    [Invariant("Lo never exceeds Hi.")]
    public bool Ordered() => Lo <= Hi;
}

[Verifiable]
public static class SpanArg
{
    public static int Width(Span s)
    {
        Spec.Requires(s.Lo >= 0 && s.Hi <= 1000, "Small bounds only.");
        Spec.Ensures<int>(r => r >= 0, "A width is never negative.");
        return s.Hi - s.Lo;
    }

    public static int Caller(int a, int b)
    {
        Spec.Requires(a >= 0 && a <= 1000 && b >= 0 && b <= 1000, "Small bounds only.");
        Spec.Ensures<int>(r => r >= 0, "Still non-negative.");
        return Width(new Span(a, b));
    }
}
