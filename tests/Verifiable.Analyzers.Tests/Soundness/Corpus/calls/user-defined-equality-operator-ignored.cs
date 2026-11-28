// A user-defined == whose body says "never equal": the analyzer treats it as structural equality instead.
// @expect: rejected
// @witness: Corpus.Calls.CustomEq.Same(1)
// @known-bug: A user-defined operator == is lowered as structural equality; its body (OperatorMethod) is ignored.
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public readonly struct Token
{
    public Token(int id) { Id = id; }
    public int Id { get; }
    public static bool operator ==(Token a, Token b) => false;
    public static bool operator !=(Token a, Token b) => true;
    public override bool Equals(object? obj) => false;
    public override int GetHashCode() => Id;
}

[Verifiable]
public static class CustomEq
{
    public static bool Same(int id)
    {
        Spec.Ensures<bool>(r => r, "A token equals itself.");
        var t = new Token(id);
        return t == t;
    }
}
