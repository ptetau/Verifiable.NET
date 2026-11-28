// Two counters meet in the middle: for (; i < j; i++, j--). The gap j - i drops by 2 per pass.
// @known-bug: LoopDecreasesStep wrongly fails: measure j - i is -1 after the last pass (n = 1), where the loop condition would exit; the step check does not use the condition
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class CommaFor
{
    public static int Run(int n)
    {
        Spec.Requires(n >= 0 && n <= 100, "Small inputs.");
        Spec.Ensures<int>(r => r >= 0 && r <= 1, "The counters meet or cross by one.");
        var i = 0;
        var j = n;
        for (; i < j; i++, j--)
        {
            Spec.LoopInvariant(i + j == n && i >= 0 && i <= j + 1, "Sum fixed, i not far past j.");
            Spec.Decreases(j - i, "The gap closes.");
        }
        return i - j;
    }
}
