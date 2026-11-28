// Custom Equals ignores B, so != on records differing only in B is false, not true.
// @expect: rejected
// @witness: Corpus.Types.NeqEq.Differ(3)
using Verifiable.Contracts;

namespace Corpus.Types;

[Verifiable]
public sealed record NPair(int A, int B)
{
    public bool Equals(NPair? other) => other is not null && A == other.A;
    public override int GetHashCode() => A;
}

[Verifiable]
public static class NeqEq
{
    public static bool Differ(int a)
    {
        Spec.Ensures<bool>(r => r == true, "Different B means !=.");
        return new NPair(a, 1) != new NPair(a, 2);
    }
}
