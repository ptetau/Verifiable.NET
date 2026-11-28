// checked(...) covers the whole expression, so (x + 1) + y throws at x == int.MaxValue even though a sibling unchecked block exists.
// @expect: rejected
// @witness: Corpus.Arithmetic.R2Sibling.Run(int.MaxValue, 0)
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class R2Sibling
{
    public static int Run(int x, int y)
    {
        Spec.Ensures<int>(r => r == unchecked(x + 1 + y), "The wrapped sum.");
        return checked(x + 1) + y;
    }
}
