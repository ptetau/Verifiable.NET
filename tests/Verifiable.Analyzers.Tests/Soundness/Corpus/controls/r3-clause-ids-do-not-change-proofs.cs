// Clause IDs link a contract to the spec sentence it came from. They never change what is proved.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Controls;

[Verifiable]
public static class ClauseTagged
{
    public static int Clamp(int x, int hi)
    {
        Spec.Requires(hi >= 0 && hi <= 1000, "The ceiling is small and non-negative.", clause: "C1");
        Spec.Ensures<int>(r => r >= 0 && r <= hi, "The result is between zero and the ceiling.", clause: "C2");
        Spec.Ensures<int>(r => x >= 0 && x <= hi ? r == x : true, "A value in range is unchanged.", "C3");
        if (x < 0) return 0;
        if (x > hi) return hi;
        return x;
    }
}
