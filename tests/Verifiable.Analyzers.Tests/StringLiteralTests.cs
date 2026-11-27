using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Verifiable.Analyzers.Smt;
using Xunit;

namespace Verifiable.Analyzers.Tests;

public class StringLiteralTests
{
    [Fact]
    public void EscapeSmtString_EncodesQuotesBackslashesControlCharsAndUnicode()
    {
        Assert.Equal("\"hello\"", ContractExpressionTranslator.EscapeSmtString("hello"));
        Assert.Equal("\"a\"\"b\"", ContractExpressionTranslator.EscapeSmtString("a\"b"));
        Assert.Equal("\"\\u{5c}u{41}\"", ContractExpressionTranslator.EscapeSmtString("\\u{41}"));
        Assert.Equal("\"\\u{9}\\u{a}\\u{d}\\u{0}\"", ContractExpressionTranslator.EscapeSmtString("\t\n\r\0"));
        Assert.Equal("\"\\u{a0}\\u{3000}\"", ContractExpressionTranslator.EscapeSmtString("\u00A0\u3000"));
    }

    [Fact]
    public async Task Utf16StringLiterals_PreserveExactLengthAndEqualityInZ3()
    {
        const string source = """
            using Verifiable.Contracts;

            namespace Sample;

            [Verifiable]
            public static class LiteralRules
            {
                public static bool CheckEscapes()
                {
                    var backslashU = @"\u{41}";
                    var whitespace = "\t\r\n\u00A0";
                    var quoted = "a\"b";
                    Spec.Ensures<bool>(r => r);
                    return backslashU.Length == 6
                        && whitespace.Length == 4
                        && quoted.Length == 3
                        && string.IsNullOrWhiteSpace(whitespace);
                }
            }
            """;

        var diags = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableGatekeeperAnalyzer(), new VerifiableSmtAnalyzer());
        Assert.DoesNotContain(diags, d => d.Id == "AD0001" || d.Id == "VER104" || d.Severity == DiagnosticSeverity.Error);
    }
}
