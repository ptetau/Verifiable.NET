// x < y is evaluated in long, so it is not the same as (uint)x < y: for x == -1 and y == 1 the first is true and the second false.
// @expect: rejected
// @witness: Corpus.Arithmetic.R2UintCompare.Lt(-1, 1u)
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class R2UintCompare
{
    public static bool Lt(int x, uint y)
    {
        Spec.Ensures<bool>(r => r == ((uint)x < y), "Compared as uint.");
        return x < y;
    }
}
