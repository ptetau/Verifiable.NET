// A tuple deconstruction inside the loop changes a, so after n iterations a == n, not 0.
// @expect: rejected
// @witness: Corpus.Control.DeconstructLoop.Run(1)
// @known-bug: Variables written by a deconstruction assignment inside a loop are not havocked at the loop head, so they keep their pre-loop value after the loop.
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class DeconstructLoop
{
    public static int Run(int n)
    {
        Spec.Requires(n >= 0 && n <= 5, "Small inputs.");
        Spec.Ensures<int>(r => r == 0, "a is never changed.");
        var a = 0;
        var b = 0;
        for (var i = 0; i < n; i++)
        {
            (a, b) = (a + 1, b);
        }
        return a;
    }
}
