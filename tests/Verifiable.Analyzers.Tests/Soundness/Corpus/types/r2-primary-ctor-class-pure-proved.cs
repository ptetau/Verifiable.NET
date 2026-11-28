// A correct program: a primary-constructor class exposing a computed property over its captured parameter.
// @expect: proved
// @known-bug: primary-constructor struct is rejected: ctor "has custom logic" (VER101) and VER103 unknown constant p_n for the captured parameter in a computed property
using Verifiable.Contracts;

namespace Corpus.Types;

[Verifiable]
public readonly struct Plus(int n)
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
        return new Plus(x).Next;
    }
}
