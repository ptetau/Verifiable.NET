// A Spec.Requires after an ordinary statement is not a precondition (VER101); callers can break it.
// @expect: rejected
// @witness: Corpus.Calls.LateRequires.Outer(0)
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public static class LateRequires
{
    public static int Inner(int x)
    {
        var y = x + 1;
        Spec.Requires(x > 0, "Positive input.");
        return y;
    }

    public static int Outer(int x)
    {
        Spec.Requires(x >= -5 && x <= 5, "Small inputs only.");
        return Inner(x);
    }
}
