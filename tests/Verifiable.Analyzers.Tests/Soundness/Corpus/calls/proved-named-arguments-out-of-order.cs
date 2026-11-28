// Named arguments in a different order from the parameters still bind by name.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public static class NamedArgs
{
    public static int Sub(int a, int b)
    {
        Spec.Requires(a >= -1000 && a <= 1000 && b >= -1000 && b <= 1000, "Small inputs only.");
        Spec.Ensures<int>(r => r == a - b, "Subtracts.");
        return a - b;
    }

    public static int Use(int x)
    {
        Spec.Requires(x >= 0 && x <= 100, "Small inputs only.");
        Spec.Ensures<int>(r => r == x - 7, "Subtracts seven.");
        return Sub(b: 7, a: x);
    }
}
