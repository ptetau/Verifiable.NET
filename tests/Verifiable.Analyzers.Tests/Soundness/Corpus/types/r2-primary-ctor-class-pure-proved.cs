// A correct program: a primary-constructor class exposing a computed property over its captured parameter.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Types;

[Verifiable]
public readonly struct Plus(short n)
{
    public int Next => n + 1;
}

[Verifiable]
public static class PlusUse
{
    public static int Go(int x)
    {
        Spec.Requires(x >= 0 && x <= 1000, "Small.");
        Spec.Ensures<int>(r => r == x + 1, "One step.");
        return new Plus((short)x).Next;
    }
}
