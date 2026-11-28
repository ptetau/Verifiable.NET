// A helper called inside a postcondition must still have its precondition met; Half needs an even input.
// @expect: rejected
// @witness: Corpus.Calls.SpecHelperPre.Halve(3)
using Verifiable.Contracts;

namespace Corpus.Calls;

[Verifiable]
public static class SpecHelperPre
{
    public static int Half(int x)
    {
        Spec.Requires(x % 2 == 0, "Only even numbers halve exactly.");
        return x / 2;
    }

    public static int Halve(int x)
    {
        Spec.Requires(x >= 0 && x <= 100, "Small inputs only.");
        Spec.Ensures<int>(r => r == Half(x), "Agrees with Half.");
        return x / 2;
    }
}
