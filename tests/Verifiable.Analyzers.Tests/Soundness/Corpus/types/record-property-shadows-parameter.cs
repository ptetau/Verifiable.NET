// The record redeclares X as X * 2, so Doubled(n).X is 2n, not n.
// @expect: rejected
// @witness: Corpus.Types.ShadowedProp.Get(1)
// @known-bug: Object creation binds record fields by constructor parameter name, ignoring an explicit property initializer (X { get; } = X * 2).
using Verifiable.Contracts;

namespace Corpus.Types;

public sealed record Doubled(int X)
{
    public int X { get; } = X * 2;
}

[Verifiable]
public static class ShadowedProp
{
    public static int Get(int n)
    {
        Spec.Requires(n >= 1 && n <= 1000, "Small inputs.");
        Spec.Ensures<int>(r => r == n, "The property is the constructor argument.");
        return new Doubled(n).X;
    }
}
