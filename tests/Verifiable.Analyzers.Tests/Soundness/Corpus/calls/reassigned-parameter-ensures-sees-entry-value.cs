// The callee reassigns its parameter; its postcondition r == x + 1 is proved against the entry value of x. The caller
// then relies on r == input + 1, which really holds. This pins down that Ensures sees entry values.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public static class ReassignedParam
{
    public static int Inc(int x)
    {
        Spec.Requires(x >= 0 && x <= 1000, "Small inputs only.");
        Spec.Ensures<int>(r => r == x + 1, "One more than the input.");
        x = x + 1;
        return x;
    }

    public static int Use(int y)
    {
        Spec.Requires(y >= 0 && y <= 1000, "Small inputs only.");
        Spec.Ensures<int>(r => r == y + 1, "One more than the input.");
        return Inc(y);
    }
}
