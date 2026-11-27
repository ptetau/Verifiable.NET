using Verifiable.Analyzers.Smt;
using Xunit;

namespace Verifiable.Analyzers.Tests;

public class StringLiteralTests
{
    [Theory]
    [InlineData("😀", 2)]
    [InlineData("\\u{61}", 6)]
    [InlineData("\0\n\"", 3)]
    public void SolverLengthMatchesDotNetCodeUnits(string value, int length)
    {
        var literal = ContractExpressionTranslator.EscapeSmtString(value);
        var result = Z3SolverRunner.Execute($"(assert (not (= (str.len {literal}) {length})))");
        Assert.Equal(SmtStatus.Unsat, result.Status);
    }
}
