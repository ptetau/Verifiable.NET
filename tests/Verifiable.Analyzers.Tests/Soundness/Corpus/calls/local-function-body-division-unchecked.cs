// A contract-free local function divides by its argument, and the enclosing method passes 0. Nothing is assumed
// about the local function's result, but its body is never checked for division by zero either.
// @expect: rejected
// @witness: Corpus.Calls.LocalFnBody.Use(0)
// @known-bug: Local function bodies are never lowered, so a division by zero inside one is not checked (contracts on local functions are ignored too).
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public static class LocalFnBody
{
    public static int Use(int x)
    {
        Spec.Requires(x >= 0 && x <= 10, "Small inputs only.");
        Spec.Ensures<int>(r => r == x, "Returns its input.");
        var t = Tenth(x);
        return x;

        static int Tenth(int y) => 10 / y;
    }
}
