// With n == int.MaxValue the loop counter i overflows at i++ (it never exceeds n), so the default no-overflow rule must reject this.
// @expect: rejected
// @no-witness: at run time i wraps and the loop never ends; nothing returns to break a contract
// @runtime: skip the witness would not terminate
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class R2LoopToMax
{
    public static int Count(int n)
    {
        Spec.Requires(n >= 0, "Not negative.");
        Spec.Ensures<int>(r => r == n, "Counts n steps.");
        var c = 0;
        for (var i = 0; i <= n; i++)
        {
            Spec.LoopInvariant(i <= n + 1 && c == i, "Counting.");
            Spec.Decreases((long)n - i + 1, "Distance to the end.");
            c = i;
        }
        return c;
    }
}
