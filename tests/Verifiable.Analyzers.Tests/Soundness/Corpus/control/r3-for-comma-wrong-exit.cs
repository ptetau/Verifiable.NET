// The measure may be negative on the exiting pass, but a wrong postcondition about the exit state is still caught.
// @expect: rejected
// @witness: Corpus.Control.R3CommaWrong.Run(1)
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class R3CommaWrong
{
    public static int Run(int n)
    {
        Spec.Requires(n >= 0 && n <= 100, "Small inputs.");
        Spec.Ensures<int>(r => r == 0, "Wrong: the counters can cross by one.");
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
