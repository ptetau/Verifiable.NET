// For odd x the recursion steps from 1 to -1, which breaks Half's own precondition x >= 0.
// @expect: rejected
// @witness: Corpus.Control.RecursivePre.Half(1)
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class RecursivePre
{
    public static int Half(int x)
    {
        Spec.Requires(x >= 0 && x <= 100, "Small non-negative input.");
        Spec.Ensures<int>(r => r >= 0, "Non-negative.");
        if (x == 0)
        {
            return 0;
        }
        return Half(x - 2) + 1;
    }
}
