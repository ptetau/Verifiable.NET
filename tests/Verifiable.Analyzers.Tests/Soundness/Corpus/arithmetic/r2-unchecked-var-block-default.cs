// An unchecked block in default mode lets x + 1 wrap, so the successor is not always greater.
// @expect: rejected
// @witness: Corpus.Arithmetic.R2UncheckedBlock.Run(int.MaxValue)
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class R2UncheckedBlock
{
    public static int Run(int x)
    {
        Spec.Ensures<int>(r => r > x, "Greater.");
        unchecked
        {
            return x + 1;
        }
    }
}
