using System.Linq;
using System.Threading.Tasks;
using Verifiable.Analyzers.Smt;
using Xunit;

namespace Verifiable.Analyzers.Tests;

/// <summary>
/// Division by a positive literal uses a simpler SMT form than the general case (and plain floor division for a
/// string length). These pin that it still means exactly what C# computes, including for negative dividends.
/// </summary>
public class LiteralDivisorTests
{
    [Theory]
    [InlineData("x / 2", -7, -3)]
    [InlineData("x % 2", -7, -1)]
    [InlineData("x / 2", 7, 3)]
    [InlineData("x % 2", 7, 1)]
    [InlineData("x / 65536", -65537, -1)]
    [InlineData("x % 65536", -65537, -1)]
    public async Task LiteralDivisor_MatchesCSharp(string expression, int input, int expected)
    {
        Assert.Equal(expected, Evaluate(expression, input));
        Assert.Empty(await RefutedAsync(expression, input, $"r == {expected}"));
        Assert.NotEmpty(await RefutedAsync(expression, input, $"r != {expected}"));
    }

    [Theory]
    [InlineData("s.Length / 65536", "s.Length % 65536")]
    public async Task StringLengthDivisor_IsExact(string quotient, string remainder)
    {
        var source = $$"""
            using Verifiable.Contracts;
            [Verifiable] public static class D {
                public static int Q(string s) { Spec.Ensures<int>(r => r * 65536 + ({{remainder}}) == s.Length && r >= 0, "digits"); Spec.Requires(s is not null, "nn"); return {{quotient}}; }
            }
            """;
        var diags = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableSmtAnalyzer());
        Assert.DoesNotContain(diags, d => d.Id is "VER102" or "VER103" or "VER101" or "AD0001");
    }

    private static int Evaluate(string expression, int x) => expression switch
    {
        "x / 2" => x / 2,
        "x % 2" => x % 2,
        "x / 65536" => x / 65536,
        "x % 65536" => x % 65536,
        _ => throw new System.ArgumentException(expression)
    };

    private static async Task<System.Collections.Generic.List<string>> RefutedAsync(string expression, int input, string claim)
    {
        var source = $$"""
            using Verifiable.Contracts;
            [Verifiable] public static class D {
                public static int F(int x) { Spec.Ensures<int>(r => {{claim}}, "claim"); Spec.Requires(x == {{input}}, "pin"); return {{expression}}; }
            }
            """;
        var diags = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableSmtAnalyzer());
        Assert.DoesNotContain(diags, d => d.Id is "VER101" or "VER103" or "AD0001");
        return diags.Where(d => d.Id == "VER102").Select(d => d.GetMessage()).ToList();
    }
}
