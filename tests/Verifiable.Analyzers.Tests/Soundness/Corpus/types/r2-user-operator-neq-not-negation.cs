// The user's != is the same as ==, so != is not the negation of ==.
// @expect: rejected
// @witness: Corpus.Types.NotNeg.Check(1)
using Verifiable.Contracts;

namespace Corpus.Types;

[Verifiable]
public readonly struct Tag
{
    public int X { get; }
    public Tag(int x) { X = x; }
    public static bool operator ==(Tag a, Tag b) => a.X == b.X;
    public static bool operator !=(Tag a, Tag b) => a.X == b.X;
    public override bool Equals(object? o) => o is Tag t && t.X == X;
    public override int GetHashCode() => X;
}

[Verifiable]
public static class NotNeg
{
    public static bool Check(int x)
    {
        Spec.Ensures<bool>(r => r == true, "!= is the negation of ==.");
        var a = new Tag(x);
        var b = new Tag(x);
        return (a != b) == !(a == b);
    }
}
