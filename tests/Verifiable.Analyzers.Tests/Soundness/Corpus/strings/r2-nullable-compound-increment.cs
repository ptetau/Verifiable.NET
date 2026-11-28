// x++ on a null int? stays null; claiming it has a value is wrong.
// @expect: rejected
// @witness: Corpus.Strings.NullInc.Bump(null)
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class NullInc
{
    public static bool Bump(int? x)
    {
        Spec.Ensures<bool>(r => r, "Claims the result has a value.");
        x++;
        return x.HasValue;
    }
}
