// k is set to x inside the if condition, so the result is x, which need not be 0.
// @expect: rejected
// @witness: Corpus.Control.AssignInCondition.Run(1)
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class AssignInCondition
{
    public static int Run(int x)
    {
        Spec.Requires(x >= 0 && x <= 10, "Small inputs.");
        Spec.Ensures<int>(r => r == 0, "k keeps its first value.");
        var k = 0;
        if ((k = x) > 100)
        {
            return 0;
        }
        return k;
    }
}
