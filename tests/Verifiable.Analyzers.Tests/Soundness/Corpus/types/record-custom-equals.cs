// The record overrides Equals to compare only A, so two records that differ in B are ==.
// @expect: rejected
// @witness: Corpus.Types.CustomEquals.Differ(1)
// @known-bug: Record == is lowered as structural SMT equality, ignoring a user-defined Equals(Pair?).
using Verifiable.Contracts;

namespace Corpus.Types;

public sealed record Pair(int A, int B)
{
    public bool Equals(Pair? other) => other is not null && A == other.A;
    public override int GetHashCode() => A;
}

[Verifiable]
public static class CustomEquals
{
    public static bool Differ(int a)
    {
        Spec.Ensures<bool>(r => r == false, "Different B means different records.");
        return new Pair(a, 1) == new Pair(a, 2);
    }
}
