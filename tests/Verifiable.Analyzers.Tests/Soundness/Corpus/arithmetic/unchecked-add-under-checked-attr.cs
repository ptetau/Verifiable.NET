// A correct program: an explicit unchecked(x + 1) wraps on purpose, and x + 1 is never equal to x.
// @expect: proved
// @known-bug: False rejection: under [CheckedArithmetic] the overflow obligation is emitted even inside an explicit unchecked(...) expression.
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
[CheckedArithmetic]
public static class UncheckedAdd
{
    public static int Next(int x)
    {
        Spec.Ensures<int>(r => r != x, "The successor differs from x.");
        return unchecked(x + 1);
    }
}
