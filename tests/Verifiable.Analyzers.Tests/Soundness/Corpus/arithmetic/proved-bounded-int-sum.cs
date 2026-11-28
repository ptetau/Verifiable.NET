// A correct program: with both inputs bounded, the int sum cannot overflow, so the default overflow check passes.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class BoundedSum
{
    public static int Add(int a, int b)
    {
        Spec.Requires(a >= 0 && a <= 1000 && b >= 0 && b <= 1000, "Small inputs only.");
        Spec.Ensures<int>(r => r >= a && r >= b && r <= 2000, "A bounded sum.");
        return a + b;
    }
}
