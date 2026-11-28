// A correct program: lo + (hi - lo) / 2 cannot overflow and stays between lo and hi.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
[CheckedArithmetic]
public static class Midpoint
{
    public static int Mid(int lo, int hi)
    {
        Spec.Requires(lo >= 0 && lo <= hi, "A valid non-negative range.");
        Spec.Ensures<int>(r => r >= lo && r <= hi, "The midpoint is inside the range.");
        return lo + (hi - lo) / 2;
    }
}
