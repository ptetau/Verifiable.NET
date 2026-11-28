// operator == is defined as <= on X, so a == b and b == a differ.
// @expect: rejected
// @witness: Corpus.Types.Asym.Sym(1, 2)
using Verifiable.Contracts;

namespace Corpus.Types;

[Verifiable]
public readonly struct Ord
{
    public int X { get; }
    public Ord(int x) { X = x; }
    public static bool operator ==(Ord a, Ord b) => a.X <= b.X;
    public static bool operator !=(Ord a, Ord b) => a.X > b.X;
    public override bool Equals(object? o) => false;
    public override int GetHashCode() => 0;
}

[Verifiable]
public static class Asym
{
    public static bool Sym(int x, int y)
    {
        Spec.Ensures<bool>(r => r == true, "Equality is symmetric.");
        var a = new Ord(x);
        var b = new Ord(y);
        return (a == b) == (b == a);
    }
}
