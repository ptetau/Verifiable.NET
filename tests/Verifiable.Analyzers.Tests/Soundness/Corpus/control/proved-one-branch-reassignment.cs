// y is reassigned on some branches only; after the merge it must take the value of the branch that ran.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class OneBranch
{
    public static int Clamp(int x)
    {
        Spec.Requires(x >= -1000 && x <= 1000, "Small inputs.");
        Spec.Ensures<int>(r => r == (x < 0 ? (-x > 100 ? 100 : -x) : x > 100 ? 100 : x), "Absolute value capped at 100.");
        var y = x;
        if (x < 0)
        {
            y = -x;
            if (y > 100)
            {
                return 100 + y - y;
            }
        }
        else if (x > 100)
        {
            y = 100;
        }
        if (y > 100)
        {
            y = 100;
        }
        return y;
    }
}
