// "not" binds tighter than "and", which binds tighter than "or": the pattern means (x <= 0) or (x in 10..19) or x == 50.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Control;

[Verifiable]
public static class Patterns
{
    public static int Classify(int x)
    {
        Spec.Requires(x >= -1000 && x <= 1000, "Small inputs.");
        Spec.Ensures<int>(r => r == ((x <= 0 || (x >= 10 && x < 20) || x == 50) ? 1 : 0), "Matches the pattern.");
        if (x is not > 0 or >= 10 and < 20 or 50)
        {
            return 1;
        }
        return 0;
    }
}
