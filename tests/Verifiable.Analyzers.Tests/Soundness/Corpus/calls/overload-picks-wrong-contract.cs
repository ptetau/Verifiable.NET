// Two overloads with different contracts; the caller calls the long overload but relies on the int one's promise.
// @expect: rejected
// @witness: Corpus.Calls.Overloads.Use(5)
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public static class Overloads
{
    public static int Twice(int x)
    {
        Spec.Requires(x >= 0 && x <= 1000, "Small inputs only.");
        Spec.Ensures<int>(r => r == x + x, "Doubles.");
        return x + x;
    }

    public static int Twice(long x)
    {
        Spec.Requires(x >= 0 && x <= 1000, "Small inputs only.");
        Spec.Ensures<int>(r => r == (int)x, "Despite the name, returns its input.");
        return (int)x;
    }

    public static int Use(int x)
    {
        Spec.Requires(x >= 1 && x <= 1000, "Small inputs only.");
        Spec.Ensures<int>(r => r == x + x, "Relies on doubling.");
        return Twice((long)x);
    }
}
