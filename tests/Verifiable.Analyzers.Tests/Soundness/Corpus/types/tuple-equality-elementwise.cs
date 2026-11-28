// A correct program: tuple == compares element by element.
// @expect: proved
// @known-bug: Tuple == (TupleBinaryOperator) is not supported, so a correct program is rejected with VER101.
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
