// An optional argument's default (0) violates the callee's precondition; the call omits it.
// @expect: rejected
// @witness: Corpus.Calls.R2Opt.Use(5)
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public static class R2Opt
{
    public static int Div(int a, int b = 0)
    {
        Spec.Requires(b != 0, "Nonzero divisor.");
        return a / b;
    }

    public static int Use(int x)
    {
        Spec.Requires(x >= 0 && x <= 100, "Small.");
        return Div(x);
    }
}
