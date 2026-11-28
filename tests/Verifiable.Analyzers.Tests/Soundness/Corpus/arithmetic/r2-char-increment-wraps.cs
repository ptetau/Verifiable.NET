// ++ on char.MaxValue wraps to 0 at run time, so the contract holds, but the default mode refuses a narrow ++ that
// loses bits (docs/manual.md, Integer overflow). Rejected by design: use unchecked(...) or [UncheckedArithmetic].
// @expect: rejected
// @no-witness: the program is correct at run time; the default mode refuses narrow wrap-around by design
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
public static class R2CharInc
{
    public static char Run(char c)
    {
        Spec.Requires(c == char.MaxValue, "At the top.");
        Spec.Ensures<char>(r => r == 0, "Wrapped to zero.");
        c++;
        return c;
    }
}
