// A default interface method promises r >= 0 but returns -1.
// @expect: rejected
// @witness: Corpus.Calls.R2IDef.Use(new Corpus.Calls.R2IDefImpl(1))
using Verifiable.Contracts;

namespace Corpus.Calls;

public interface IR2Def
{
    int Get()
    {
        Spec.Ensures<int>(r => r >= 0, "Non-negative.");
        return -1;
    }
}

[Verifiable]
public sealed record R2IDefImpl(int A) : IR2Def;

[Verifiable]
public static class R2IDef
{
    public static int Use(IR2Def d)
    {
        Spec.Ensures<int>(r => r >= 0, "Trusts the default method.");
        return d.Get();
    }
}
