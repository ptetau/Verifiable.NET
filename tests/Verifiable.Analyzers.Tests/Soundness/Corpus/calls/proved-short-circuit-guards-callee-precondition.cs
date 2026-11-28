// A correct program: the && guard makes Inv's precondition hold, and Inv's postcondition pins the answer.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public static class GuardedCall
{
    public static int Inv(int x)
    {
        Spec.Requires(x != 0, "No division by zero.");
        Spec.Requires(x >= -1000 && x <= 1000, "Small inputs only.");
        Spec.Ensures<int>(r => r == 100 / x, "Integer quotient.");
        return 100 / x;
    }

    public static bool SmallPositive(int x)
    {
        Spec.Requires(x >= -1000 && x <= 1000, "Small inputs only.");
        Spec.Ensures<bool>(r => r == (x > 0 && x <= 100), "Positive and at most 100.");
        return x != 0 && Inv(x) > 0;
    }
}
