// The explicit constructor swaps its arguments; the analyzer maps constructor parameters to properties by name.
// @expect: rejected
// @witness: Corpus.Calls.SwappedCtor.First(1, 2)
// @known-bug: Object creation maps constructor parameters to same-named properties and ignores the constructor body.
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public readonly record struct Pair
{
    public Pair(int a, int b)
    {
        A = b;
        B = a;
    }

    public int A { get; }
    public int B { get; }
}

[Verifiable]
public static class SwappedCtor
{
    public static int First(int a, int b)
    {
        Spec.Requires(a >= 0 && a <= 100 && b >= 0 && b <= 100, "Small inputs only.");
        Spec.Ensures<int>(r => r == a, "The first component is the first argument.");
        var p = new Pair(a, b);
        return p.A;
    }
}
