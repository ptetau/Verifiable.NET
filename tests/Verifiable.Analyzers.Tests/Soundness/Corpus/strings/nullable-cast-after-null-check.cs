// Correct: (int)x is safe once x is known to be non-null.
// @expect: proved
// @known-bug: false rejection: an explicit (int) cast of an int? keeps the Opt sort, so Z3 reports a sort error (VER103)
using Verifiable.Contracts;

namespace Corpus.Strings;

[Verifiable]
public static class GuardedNullableCast
{
    public static int UnwrapOrZero(int? x)
    {
        Spec.Ensures<int>(r => r == (x == null ? 0 : x.Value), "The payload, or 0.");
        if (x == null)
        {
            return 0;
        }
        return (int)x;
    }
}
