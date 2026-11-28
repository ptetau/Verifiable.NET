// A [PureAxiom] promises a witness index in [0, n); at n == 0 that is impossible, so every proof after that call is
// vacuous. The call is on one branch only and must still be flagged (VER105).
// @expect: rejected
// @witness: Corpus.Calls.AxiomEmptyRange.Use(1)
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public static class AxiomEmptyRange
{
    [PureAxiom]
    public static int IndexIn(int n)
    {
        Spec.Requires(n >= 0, "Non-negative length.");
        Spec.Ensures<int>(r => Spec.Exists(0, n, i => i == r), "The result is an index in [0, n).");
        return 0;
    }

    public static int Use(int x)
    {
        Spec.Requires(x >= -10 && x <= 10, "Small inputs only.");
        Spec.Ensures<int>(r => r == 7, "Always seven.");
        if (x > 0)
        {
            return IndexIn(0);
        }

        return 7;
    }
}
