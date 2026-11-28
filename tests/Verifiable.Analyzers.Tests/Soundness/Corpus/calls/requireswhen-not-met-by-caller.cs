// The callee requires d != 0 only when divide is true; the caller passes divide = true and d = 0.
// @expect: rejected
// @witness: Corpus.Calls.RequiresWhenCaller.Use(0)
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public static class RequiresWhenCaller
{
    public static int Scale(int n, int d, bool divide)
    {
        Spec.RequiresWhen(divide, d != 0, "Dividing needs a non-zero divisor.");
        Spec.Requires(n >= 0 && n <= 1000 && d >= 0 && d <= 1000, "Small inputs only.");
        return divide ? n / d : n * d;
    }

    public static int Use(int d)
    {
        Spec.Requires(d >= 0 && d <= 10, "Small inputs only.");
        return Scale(100, d, true);
    }
}
