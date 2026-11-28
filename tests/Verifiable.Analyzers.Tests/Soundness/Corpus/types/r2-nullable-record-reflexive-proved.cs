// A correct program: a nullable record equals itself, null or not.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Types;

public sealed record NPt(int X, int Y);

[Verifiable]
public static class NullRec
{
    public static bool Refl(int x, int y)
    {
        Spec.Ensures<bool>(r => r == true, "A value equals itself.");
        NPt? a = x > 0 ? new NPt(x, y) : null;
        NPt? b = a;
        return a == b && !(a != b);
    }
}
