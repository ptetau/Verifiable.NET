// F calls itself through a static readonly delegate with the same argument, so it never returns for x > 0.
// @expect: rejected
// @witness: Corpus.Control.ViaDelegate.F(1)
using System;
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class ViaDelegate
{
    static readonly Func<int, int> Self = F;

    public static int F(int x)
    {
        Spec.Requires(x >= 0 && x <= 10, "Small inputs.");
        Spec.Ensures<int>(r => r == 0, "Zero.");
        Spec.Decreases(x, "x.");
        if (x == 0)
        {
            return 0;
        }
        return Self(x);
    }
}
