// A correct program: an explicit unchecked((sbyte)x) wraps inside a [CheckedArithmetic] scope.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
[CheckedArithmetic]
public static class R2UncheckedNarrow
{
    public static sbyte Run(int x)
    {
        Spec.Requires(x == 200, "x is 200.");
        Spec.Ensures<sbyte>(r => r == -56, "Wrapped.");
        return unchecked((sbyte)x);
    }
}
