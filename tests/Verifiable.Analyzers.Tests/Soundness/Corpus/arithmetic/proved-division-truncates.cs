// A correct program: C# division truncates toward zero and % keeps the dividend's sign, so q * 2 + x % 2 == x.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class Halve
{
    public static int Half(int x)
    {
        Spec.Requires(x >= -1000 && x <= 1000, "Small input.");
        Spec.Ensures<int>(r => r * 2 + x % 2 == x && (x >= 0 ? r >= 0 : r <= 0), "Truncating division by two.");
        return x / 2;
    }
}
