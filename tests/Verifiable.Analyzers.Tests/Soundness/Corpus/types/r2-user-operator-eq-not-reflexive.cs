// The class defines == so that nothing equals anything, not even itself.
// @expect: rejected
// @witness: Corpus.Types.NonRefl.Same(1)
using Verifiable.Contracts;

namespace Corpus.Types;

[Verifiable]
public readonly struct Never
{
    public int X { get; }
    public Never(int x) { X = x; }
    public static bool operator ==(Never a, Never b) => false;
    public static bool operator !=(Never a, Never b) => true;
    public override bool Equals(object? o) => false;
    public override int GetHashCode() => 0;
}

[Verifiable]
public static class NonRefl
{
    public static bool Same(int x)
    {
        Spec.Ensures<bool>(r => r == true, "A value equals itself.");
        var n = new Never(x);
        return n == n;
    }
}
