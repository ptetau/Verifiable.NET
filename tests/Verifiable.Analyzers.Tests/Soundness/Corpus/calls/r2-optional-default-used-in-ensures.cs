// The default value of an optional parameter (7) is used by the caller; the postcondition about it is right.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public static class R2OptOk
{
    public static int Add(int a, int b = 7)
    {
        Spec.Requires(a >= 0 && a <= 1000 && b >= 0 && b <= 1000, "Small.");
        Spec.Ensures<int>(r => r == a + b, "Adds.");
        return a + b;
    }

    public static int Use(int x)
    {
        Spec.Requires(x >= 0 && x <= 100, "Small.");
        Spec.Ensures<int>(r => r == x + 7, "Adds seven.");
        return Add(x);
    }
}
