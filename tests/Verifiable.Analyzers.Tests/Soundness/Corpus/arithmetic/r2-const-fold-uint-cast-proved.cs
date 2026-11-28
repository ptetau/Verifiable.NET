// A correct program: unchecked((int)3000000000u) is a constant that wraps to -1294967296.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class R2ConstCast
{
    public static int Run()
    {
        Spec.Ensures<int>(r => r == -1294967296, "Wrapped constant.");
        return unchecked((int)3000000000u);
    }
}
