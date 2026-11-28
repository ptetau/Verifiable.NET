// A record's == calls its own Equals. This Equals says every two values are equal, but the analyzer
// compares the fields instead.
// @expect: rejected
// @witness: Corpus.Calls.RecordEq.Same(1, 2)
// @known-bug: A record's synthesized == is lowered as field-by-field equality (IsBuiltInValueOperator / structural =), ignoring a user-declared Equals that it calls at run time.
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public readonly record struct Loose(int V)
{
    public bool Equals(Loose other) => true;
    public override int GetHashCode() => 0;
}

[Verifiable]
public static class RecordEq
{
    public static bool Same(int a, int b)
    {
        Spec.Requires(a >= 0 && a <= 10 && b >= 0 && b <= 10, "Small inputs only.");
        Spec.Ensures<bool>(r => r == (a == b), "Equal exactly when the fields are equal.");
        return new Loose(a) == new Loose(b);
    }
}
