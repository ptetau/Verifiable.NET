// F reaches itself through the property Back (which calls F(3)); F(1) never returns, so "r == 0" is not earned.
// @expect: rejected
// @witness: Corpus.Control.ViaProperty.F(1)
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class ViaProperty
{
    static int Back => F(3);

    public static int F(int x)
    {
        Spec.Requires(x >= 0 && x <= 10, "Small inputs.");
        Spec.Ensures<int>(r => r == 0, "Zero.");
        Spec.Decreases(x, "x.");
        if (x == 0)
        {
            return 0;
        }
        return Back;
    }
}
