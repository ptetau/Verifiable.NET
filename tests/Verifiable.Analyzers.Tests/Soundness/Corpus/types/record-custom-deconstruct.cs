// The record declares its own Deconstruct that swaps the parts, so var (a, b) = p gives a == p.B.
// @expect: rejected
// @witness: Corpus.Types.CustomDeconstruct.First(new Corpus.Types.Duo(1, 2))
using Verifiable.Contracts;

namespace Corpus.Types;

public sealed record Duo(int A, int B)
{
    public void Deconstruct(out int A, out int B)
    {
        A = this.B;
        B = this.A;
    }
}

[Verifiable]
public static class CustomDeconstruct
{
    public static int First(Duo p)
    {
        Spec.Requires(p.A != p.B, "Distinct parts.");
        Spec.Ensures<int>(r => r == p.A, "The first part is A.");
        var (a, _) = p;
        return a;
    }
}
