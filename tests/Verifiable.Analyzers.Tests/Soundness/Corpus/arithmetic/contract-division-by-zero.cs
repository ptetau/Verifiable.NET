// The postcondition r == x / y throws DivideByZeroException when y is 0, so it is not a true statement.
// @expect: rejected
// @witness: Corpus.Arithmetic.ContractDivZero.Quotient(1, 0)
using Verifiable.Contracts;

namespace Corpus.Arithmetic;

[Verifiable]
[CheckedArithmetic]
public static class ContractDivZero
{
    public static int Quotient(int x, int y)
    {
        Spec.Requires(x >= 0, "Non-negative dividend.");
        Spec.Ensures<int>(r => r == x / y, "Result is the quotient.");
        return y == 0 ? -1 : x / y;
    }
}
