// The default interface method has NO contract and returns -1; a caller through the interface must not assume anything about it.
// @expect: rejected
// @witness: Corpus.Calls.R2IDefNo.Use(new Corpus.Calls.R2IDefNoImpl(1))
using Verifiable.Contracts;

namespace Corpus.Calls;

public interface IR2DefNo
{
    int Get() => -1;
}

[Verifiable]
public sealed record R2IDefNoImpl(int A) : IR2DefNo;

[Verifiable]
public static class R2IDefNo
{
    public static int Use(IR2DefNo d)
    {
        Spec.Ensures<int>(r => r == 0, "Zero.");
        return d.Get();
    }
}
