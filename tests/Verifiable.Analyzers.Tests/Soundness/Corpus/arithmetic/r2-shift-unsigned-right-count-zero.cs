// x >>> 0 is x, so for a negative x the result is negative.
// @expect: rejected
// @witness: Corpus.Arithmetic.R2UnsignedShiftZero.Run(-1, 32)
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class R2UnsignedShiftZero
{
    public static int Run(int x, int n)
    {
        Spec.Requires(n == 32, "The count wraps to 0.");
        Spec.Ensures<int>(r => r >= 0, "Not negative.");
        return x >>> n;
    }
}
