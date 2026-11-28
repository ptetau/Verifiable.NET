// Two [PureAxiom]s are consistent alone but contradict for x > 0: F(x) > 0 and G(x) < 0 with F(x) == G(x) isn't stated, so use one: H promises r > x and r < x only when x > 5.
// @expect: rejected
// @witness: Corpus.Calls.R2AxSome.Use(7)
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public static class R2AxSome
{
    [PureAxiom]
    public static int H(int x)
    {
        Spec.Requires(x >= 0 && x <= 100, "Small.");
        Spec.EnsuresWhen<int>(x > 5, r => r > x, "Above x for big x.");
        Spec.EnsuresWhen<int>(x > 5, r => r < x, "Below x for big x.");
        return x;
    }

    public static int Use(int a)
    {
        Spec.Requires(a >= 0 && a <= 100, "Small.");
        Spec.Ensures<int>(r => r == 12345, "Whatever.");
        if (a > 5)
        {
            return H(a);
        }

        return 12345;
    }
}
