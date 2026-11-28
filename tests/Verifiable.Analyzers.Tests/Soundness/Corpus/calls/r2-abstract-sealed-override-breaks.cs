// A virtual method promises r >= 1; a sealed override two levels down returns 0. The caller relies on the promise.
// @expect: rejected
// @witness: Corpus.Calls.R2Abs.Use(new Corpus.Calls.R2AbsImpl(1))
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public abstract record R2AbsBase(int A)
{
    public abstract int Next();
}

[Verifiable]
public record R2AbsMid(int A) : R2AbsBase(A)
{
    public override int Next()
    {
        Spec.Ensures<int>(r => r >= 1, "Positive.");
        return 1;
    }
}

[Verifiable]
public sealed record R2AbsImpl(int A) : R2AbsMid(A)
{
    public sealed override int Next() => 0;
}

[Verifiable]
public static class R2Abs
{
    public static int Use(R2AbsMid m)
    {
        Spec.Ensures<int>(r => r >= 1, "Relies on the base promise.");
        return m.Next();
    }
}
