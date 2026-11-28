// A static constructor assigns a static readonly field to 7; the method claims the field is 0.
// @expect: rejected
// @witness: Corpus.Calls.R2StaticCtor.Get()
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public static class R2StaticCtor
{
    private static readonly int K;

    static R2StaticCtor()
    {
        K = 7;
    }

    public static int Get()
    {
        Spec.Ensures<int>(r => r == 0, "Claims zero.");
        return K;
    }
}
