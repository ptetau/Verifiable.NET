// A correct program: int < uint compares as long in C#, so -1 < 1u.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class R2MixedCompare
{
    public static bool Lt(int x, uint y)
    {
        Spec.Ensures<bool>(r => r == ((long)x < (long)y), "Compared as long.");
        return x < y;
    }
}
