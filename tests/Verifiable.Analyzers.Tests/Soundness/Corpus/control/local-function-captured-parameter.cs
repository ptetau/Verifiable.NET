// AddX reads the captured x when it is called, after x = x + 1, so the two uses of x cancel and the result is 0.
// @expect: rejected
// @witness: Corpus.Control.CapturedParam.Run(0)
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class CapturedParam
{
    public static int Run(int x)
    {
        Spec.Requires(x >= 0 && x <= 10, "Small inputs.");
        Spec.Ensures<int>(r => r == -1, "AddX sees the old x.");
        int AddX(int y) => y + x;
        x = x + 1;
        return AddX(0) - x;
    }
}
