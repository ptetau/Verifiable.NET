// A method in an [Unverifiable] class is never checked, so its Spec.Ensures must not be trusted (VER006).
// @expect: rejected
// @witness: Corpus.Calls.UsesUnverified.Grow(3)
using Verifiable.Contracts;

namespace Corpus.Calls;

[Unverifiable]
public static class UncheckedLib
{
    public static int Grow(int x)
    {
        Spec.Ensures<int>(r => r > x, "Claims to grow, but is never checked.");
        var y = x;
        return y;
    }
}

[Verifiable]
public static class UsesUnverified
{
    public static int Grow(int x)
    {
        Spec.Requires(x >= 0 && x <= 1000, "Small inputs only.");
        Spec.Ensures<int>(r => r > x, "Relies on UncheckedLib.Grow.");
        return UncheckedLib.Grow(x);
    }
}
