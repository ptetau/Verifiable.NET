// The constructor clamps negative input to 0, so new Clamped(n).N is not n when n < 0.
// @expect: rejected
// @witness: Corpus.Types.ExplicitCtor.Get(-5)
using Verifiable.Contracts;

namespace Corpus.Types;

public readonly struct Clamped
{
    public readonly int N;

    public Clamped(int n)
    {
        N = n < 0 ? 0 : n;
    }
}

[Verifiable]
public static class ExplicitCtor
{
    public static int Get(int n)
    {
        Spec.Requires(n >= -1000 && n <= 1000, "Small inputs.");
        Spec.Ensures<int>(r => r == n, "The field holds the argument.");
        return new Clamped(n).N;
    }
}
