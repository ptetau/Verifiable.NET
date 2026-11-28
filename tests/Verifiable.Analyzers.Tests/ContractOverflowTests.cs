using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Verifiable.Analyzers.Smt;
using Xunit;

namespace Verifiable.Analyzers.Tests;

/// <summary>Arithmetic inside contracts is overflow-checked like code arithmetic (#48).</summary>
public class ContractOverflowTests
{
    private static async Task<List<string>> AnalyzeAsync(string attribute, string method)
    {
        var source = $$"""
            using System;
            using Verifiable.Contracts;
            [Verifiable] {{attribute}}
            public static class A
            {
                {{method}}
            }
            """;
        var diags = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableSmtAnalyzer());
        var problems = diags.Where(d => d.Id is "VER101" or "VER103" or "AD0001").Select(d => d.Id + ": " + d.GetMessage()).ToList();
        Assert.True(problems.Count == 0, string.Join("\n", problems));
        return diags.Where(d => d.Id == "VER102").Select(d => d.GetMessage()).ToList();
    }

    [Theory]
    [InlineData("public static int F(int x) { Spec.Requires(x + x >= 0, \"p\"); return x; }")]
    [InlineData("public static int F(int x) { Spec.Ensures<int>(r => r + 1 > r, \"p\"); return x; }")]
    [InlineData("public static int F(int x) { Spec.Requires(x >= 0, \"p\"); Spec.Ensures<int>(r => r * 2 >= 0, \"p\"); return x; }")]
    [InlineData("public static bool F(int n) { Spec.Ensures<bool>(r => Spec.Exists(0, n + 1, i => i == n), \"p\"); return true; }")]
    [InlineData("public static int F(int n) { Spec.Requires(n >= 0, \"p\"); Spec.Decreases(n * 2, \"m\"); return n == 0 ? 0 : F(n - 1); }")]
    [InlineData("public static int F(int x) { Spec.Requires(-x <= 0, \"p\"); return x; }")]
    public async Task OverflowInAContract_IsRejected(string method)
    {
        var errors = await AnalyzeAsync("", method);
        Assert.Contains(errors, e => e.Contains("Overflow", System.StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("public static int F(int x) { Spec.Requires(x >= 0 && x <= 1000, \"p\"); Spec.Ensures<int>(r => r + 1 > r, \"p\"); return x; }")]
    [InlineData("public static int F(int x) { Spec.Requires(x >= -1000 && x <= 1000 && x + x >= 0, \"p\"); return x; }")]
    [InlineData("public static int F(int x) { Spec.Requires(x >= 0, \"p\"); Spec.Requires(x <= 1000, \"p\"); Spec.Requires(x + x >= 0, \"p\"); return x; }")]
    [InlineData("public static bool F(int n) { Spec.Requires(n >= 0 && n < 1000, \"p\"); Spec.Ensures<bool>(r => Spec.Exists(0, n + 1, i => i + 1 > 0 || i == n), \"p\"); return true; }")]
    public async Task BoundedContractArithmetic_IsAccepted(string method)
    {
        Assert.Empty(await AnalyzeAsync("", method));
    }

    [Fact]
    public async Task CalleeRequires_IsCheckedAtTheCall()
    {
        var errors = await AnalyzeAsync("", """
            public static int G(int x) { Spec.Requires(x >= 0 && x <= 1000, "small"); Spec.Requires(x + x >= 0, "p"); return x; }
            public static int F(int x) { Spec.Requires(x >= 0 && x <= 1000, "small"); return G(x); }
            """);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task QuantifierBody_IsChecked()
    {
        var errors = await AnalyzeAsync("", "public static bool F(int n) { Spec.Requires(n >= 0 && n <= 10, \"p\"); Spec.Ensures<bool>(r => Spec.ForAll(0, n, i => i * 1000000000 >= 0), \"p\"); return true; }");
        Assert.NotEmpty(errors);
    }

    [Fact]
    public async Task UncheckedArithmetic_WrapsInContracts_NoObligation()
    {
        var errors = await AnalyzeAsync("[UncheckedArithmetic]", "public static int F(int x) { Spec.Ensures<int>(r => r + 1 > r || r == int.MaxValue, \"p\"); return x; }");
        Assert.Empty(errors);
        var refuted = await AnalyzeAsync("[UncheckedArithmetic]", "public static int F(int x) { Spec.Ensures<int>(r => r + 1 > r, \"p\"); return x; }");
        Assert.NotEmpty(refuted);
    }
}
