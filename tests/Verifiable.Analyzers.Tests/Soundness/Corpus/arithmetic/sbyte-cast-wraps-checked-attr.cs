// Even under [CheckedArithmetic], an unchecked (sbyte) cast wraps: (sbyte)128 is -128.
// @expect: rejected
// @witness: Corpus.Arithmetic.SbyteWrap.ToSigned(128)
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
[CheckedArithmetic]
public static class SbyteWrap
{
    public static int ToSigned(int x)
    {
        Spec.Requires(x >= 0 && x < 256, "A byte-sized input.");
        Spec.Ensures<int>(r => r >= 0, "The result is not negative.");
        return (sbyte)x;
    }
}
