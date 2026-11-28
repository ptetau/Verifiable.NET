// Correct: (int)x is safe once x is known to be non-null.
// @expect: proved
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
