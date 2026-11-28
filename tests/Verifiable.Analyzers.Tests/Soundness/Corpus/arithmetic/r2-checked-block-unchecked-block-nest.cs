// A correct program: the inner unchecked(...) wraps even inside a checked(...) block, and the outer addition of 0 cannot overflow.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class R2Nest
{
    public static int Run(int x)
    {
        Spec.Ensures<int>(r => (x == int.MaxValue && r == int.MinValue) || r == x + 1, "Wraps at the top.");
        return checked(unchecked(x + 1) + 0);
    }
}
