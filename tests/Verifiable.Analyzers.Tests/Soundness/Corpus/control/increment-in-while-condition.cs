// The loop condition itself bumps i, so the loop runs until i reaches n; i is n afterwards, not 1.
// @expect: rejected
// @witness: Corpus.Control.ConditionIncrement.Run(3)
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class ConditionIncrement
{
    public static int Run(int n)
    {
        Spec.Requires(n >= 1 && n <= 5, "Small inputs.");
        Spec.Ensures<int>(r => r == 1, "The loop stops after one bump.");
        var i = 0;
        while ((i = i + 1) < n)
        {
            Spec.Decreases(n - i, "i moves toward n.");
        }
        return i;
    }
}
