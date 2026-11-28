// A lambda created locally captures a variable and is invoked; the contract must not be trusted blindly.
// @expect: rejected
// @witness: Corpus.Calls.R2LamLocal.Use(5)
using System;
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public static class R2LamLocal
{
    public static int Use(int x)
    {
        Spec.Requires(x >= 0 && x <= 100, "Small.");
        Spec.Ensures<int>(r => r == x, "Identity.");
        Func<int> f = () => x + 1;
        return f();
    }
}
