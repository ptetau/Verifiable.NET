// k is bumped by the if condition on every pass, so after n passes k == n, not 0.
// @expect: rejected
// @witness: Corpus.Control.BodyConditionIncrement.Run(2)
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class BodyConditionIncrement
{
    public static int Run(int n)
    {
        Spec.Requires(n >= 0 && n <= 5, "Small inputs.");
        Spec.Ensures<int>(r => r == 0, "k is never bumped.");
        var k = 0;
        for (var i = 0; i < n; i++)
        {
            if ((k = k + 1) > 100)
            {
                return 0;
            }
        }
        return k;
    }
}
