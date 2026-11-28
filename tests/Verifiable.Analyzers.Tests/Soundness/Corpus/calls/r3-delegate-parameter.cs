// A Func<> parameter is invoked; its result is unknown, so the postcondition can't be proved.
// @expect: rejected
// @witness: Corpus.Calls.R2Lam.Use(x => -1)
using System;
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public static class R2Lam
{
    public static int Use(Func<int, int> f)
    {
        Spec.Ensures<int>(r => r >= 0, "Non-negative.");
        return f(3);
    }
}
