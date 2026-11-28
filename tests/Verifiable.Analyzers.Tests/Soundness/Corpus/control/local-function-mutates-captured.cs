// The local function Bump changes the captured c, so c is 1 after the call, not 0.
// @expect: rejected
// @witness: Corpus.Control.LocalCapture.Run(0)
// @known-bug: Calling a local function drops its writes to captured locals, so c keeps its old value after Bump().
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class LocalCapture
{
    public static int Run(int x)
    {
        Spec.Requires(x >= 0 && x <= 10, "Small inputs.");
        Spec.Ensures<int>(r => r == x, "c is untouched.");
        var c = x;
        int Bump() => c = c + 1;
        Bump();
        return c;
    }
}
