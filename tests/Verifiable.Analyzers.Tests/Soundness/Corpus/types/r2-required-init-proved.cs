// A correct program: a required init member set by an object initializer.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Types;

[Verifiable]
public sealed record Req
{
    public required int A { get; init; }
}

[Verifiable]
public static class ReqUse
{
    public static int Make(int x)
    {
        Spec.Ensures<int>(r => r == x, "A is x.");
        return new Req { A = x }.A;
    }
}
