// A correct program: tuple == compares element by element.
// @expect: proved
using Verifiable.Contracts;

namespace Corpus.Types;

[Verifiable]
public static class TupleEquality
{
    public static bool Same(int a, int b)
    {
        Spec.Ensures<bool>(r => r == (a == b), "(a, b) == (b, a) exactly when a == b.");
        return (a, b) == (b, a);
    }
}
