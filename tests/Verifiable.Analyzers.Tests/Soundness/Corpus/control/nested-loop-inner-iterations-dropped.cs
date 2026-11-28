// The inner loop runs n times per outer iteration, so c ends up n, not 0.
// @expect: rejected
// @witness: Corpus.Control.NestedLoop.Count(1)
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class NestedLoop
{
    public static int Count(int n)
    {
        Spec.Requires(n >= 0 && n <= 5, "Small inputs.");
        Spec.Ensures<int>(r => r == 0, "Nothing is counted.");
        var c = 0;
        for (var i = 0; i < 1; i++)
        {
            Spec.LoopInvariant(c == 0, "Still nothing counted.");
            var j = 0;
            while (j < n)
            {
                Spec.LoopInvariant(j <= n, "j stays in range.");
                Spec.Decreases(n - j, "j moves toward n.");
                c = c + 1;
                j = j + 1;
            }
        }
        return c;
    }
}
