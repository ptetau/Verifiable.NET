using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Verifiable.Analyzers.Smt;
using Xunit;

namespace Verifiable.Analyzers.Tests;

public class StringsTests
{
    [Fact]
    public async Task ShortCircuitGuardsTheNonNullObligationInSpecifications() =>
        await Check("""
            static bool Match(string? s)
            {
                Spec.Ensures<bool>(r => r == (s != null && s.Contains("a")));
                return s != null && s.Contains("a");
            }
            """);

    [Fact]
    public async Task WhitespaceIncludesNullAndUnicodeButNotZeroWidthSpace() =>
        await Check("""
            static bool Blank(string? s)
            {
                Spec.Ensures<bool>(r => r == string.IsNullOrWhiteSpace(s) &&
                    (s != null && s != "\u0085\u2003\t" || r) && (s != "\u200b" || !r));
                return string.IsNullOrWhiteSpace(s);
            }
            """);

    [Fact]
    public async Task TrimRemovesOnlyTheWhitespaceAtEachEnd() =>
        await Check("""
            static string Clean(string? s)
            {
                Spec.Requires(s == " \tA  B\u3000");
                Spec.Ensures<string>(r => r == s!.Trim() && r == "A  B");
                return s!.Trim();
            }
            """);

    [Fact]
    public async Task OrdinalSearchUsesTheFullReceiverAndCorrectDirection() =>
        await Check("""
            static bool Search(string? s)
            {
                Spec.Requires(s == "abcde");
                Spec.Ensures<bool>(r => r && s!.Contains("bcd", StringComparison.Ordinal)
                    && !s.EndsWith("a", StringComparison.Ordinal) && !s.Contains("B"));
                return s!.StartsWith("ab", StringComparison.Ordinal) && s.EndsWith('e') && s.Contains('c');
            }
            """);

    [Fact]
    public async Task JoinRetainsSeparatorsAndTreatsNullAsEmpty() =>
        await Check("""
            static string Combine(string? a)
            {
                Spec.Requires(a == "value");
                Spec.Ensures<string>(r => r == ",value," && r == string.Join(",", null, a, null)
                    && string.Join((string?)null, a, a) == "valuevalue"
                    && string.Join(",", new string[] { }) == string.Empty);
                return string.Join(",", new string?[] { null, a, null });
            }
            """);

    [Theory]
    [InlineData("static bool C(string s) => s.StartsWith(\"a\");")]
    [InlineData("static bool C(string s) => s.Contains(\"a\", StringComparison.OrdinalIgnoreCase);")]
    [InlineData("static bool C(string s, StringComparison c) => s.EndsWith(\"a\", c);")]
    public async Task UnsupportedOverloadsAndUnboundedCallsAreNotTrusted(string method) =>
        await Check(method, "VER006", "VER101");

    [Fact]
    public async Task JoinDoesNotAcceptAliasedArrays() =>
        await Check("static string C(string[] s) => string.Join(\",\", s);", "VER002", "VER006", "VER101");

    [Fact]
    public async Task OverLimitParamsJoinIsNotTrusted() =>
        await Check($"static string J() => string.Join(\",\", {string.Join(", ", Enumerable.Repeat("\"a\"", 33))});", "VER006", "VER101");

    [Theory]
    [InlineData("static bool C(string? s) { Spec.Ensures<bool>(r => r); return s!.Contains(\"\"); }")]
    [InlineData("static string C(string? s) => s!.Trim();")]
    [InlineData("static bool C(string? s) { Spec.Ensures<bool>(r => r == \"abc\".Contains(s!)); return true; }")]
    [InlineData("static string C() { Spec.Ensures<string>(r => r == \"ab\"); return \" a b \".Trim(); }")]
    public async Task NullCallsAndWrongPostconditionsDoNotVerify(string method) =>
        await Check(method, "VER102");

    private static async Task Check(string method, params string[] expectedErrors)
    {
        var source = "using System; using Verifiable.Contracts; [Verifiable] static class Test { " + method + " }";
        var diagnostics = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableGatekeeperAnalyzer(), new VerifiableSmtAnalyzer());
        Assert.Empty(diagnostics.Where(d => (d.Id is "VER104" or "AD0001" || d.Severity == DiagnosticSeverity.Error) &&
            !expectedErrors.Contains(d.Id)));
        foreach (var expected in expectedErrors) Assert.Contains(diagnostics, d => d.Id == expected);
    }
}
