// Implies(a, Implies(b, c)) is right-nested: this is correct (x > 0 and x > 1 imply x > 0).
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public static class R2ImpNest
{
    public static int Run(int x)
    {
        Spec.Requires(x >= -100 && x <= 100, "Small.");
        Spec.Ensures<int>(r => Spec.Implies(r > 0, Spec.Implies(r > 1, r > 0)) && Spec.Implies(Spec.Implies(r > 0, r > 1), r != 1), "Nested.");
        return x;
    }
}
