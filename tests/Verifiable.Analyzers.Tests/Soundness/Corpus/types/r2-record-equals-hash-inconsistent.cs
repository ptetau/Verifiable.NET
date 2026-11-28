// The record's Equals compares A only but GetHashCode mixes B in, so equal records can hash differently.
// @expect: rejected
// @witness: Corpus.Types.HashEq.SameHash(new Corpus.Types.HPair(1, 1), new Corpus.Types.HPair(1, 2))
using Verifiable.Contracts;

namespace Corpus.Types;

[Verifiable]
public sealed record HPair(int A, int B)
{
    public bool Equals(HPair? other) => other is not null && A == other.A;
    public override int GetHashCode() => A + 31 * B;
}

[Verifiable]
public static class HashEq
{
    public static bool SameHash(HPair p, HPair q)
    {
        Spec.Requires(p.Equals(q), "Equal records.");
        Spec.Ensures<bool>(r => r == true, "Equal records hash alike.");
        return p.GetHashCode() == q.GetHashCode();
    }
}
