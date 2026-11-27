using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Verifiable.Analyzers.Smt;
using Xunit;

namespace Verifiable.Analyzers.Tests;

/// <summary>
/// Without Z3 no proof is checked, so a missing solver must stop the build unless the project opts out.
/// </summary>
public class MissingSolverTests
{
    private const string Source = """
        using Verifiable.Contracts;

        [Verifiable]
        public static class Ledger
        {
            public static decimal Withdraw(decimal balance, decimal amount)
            {
                Spec.Ensures<decimal>(r => r >= 0m, "Balances never go negative.");
                return balance - amount; // wrong, but nobody can tell without Z3
            }
        }
        """;

    private static async Task<IReadOnlyList<Diagnostic>> AnalyzeWithoutSolverAsync(IReadOnlyDictionary<string, string>? props)
    {
        Z3SolverRunner.SimulateMissingSolver.Value = true;
        try
        {
            var diags = await AnalyzerTestHarness.AnalyzeWithOptionsAsync(Source, props, new VerifiableSmtAnalyzer());
            return diags.Where(d => d.Id.StartsWith("VER", System.StringComparison.Ordinal)).ToList();
        }
        finally
        {
            Z3SolverRunner.SimulateMissingSolver.Value = false;
        }
    }

    [Fact]
    public async Task MissingSolver_IsABuildError_ByDefault()
    {
        var diags = await AnalyzeWithoutSolverAsync(null);

        var missing = Assert.Single(diags, d => d.Id == "VER104");
        Assert.Equal(DiagnosticSeverity.Error, missing.Severity);
        Assert.Contains("VerifiableAllowMissingZ3", missing.GetMessage());
        Assert.DoesNotContain(diags, d => d.Id == "VER102");
    }

    [Theory]
    [InlineData("true")]
    [InlineData("1")]
    public async Task MissingSolver_IsAWarning_WhenProjectOptsOut(string value)
    {
        var diags = await AnalyzeWithoutSolverAsync(new Dictionary<string, string>
        {
            ["build_property.VerifiableAllowMissingZ3"] = value
        });

        var missing = Assert.Single(diags, d => d.Id == "VER104");
        Assert.Equal(DiagnosticSeverity.Warning, missing.Severity);
    }

    [Fact]
    public async Task WithSolver_TheSameBugIsCaught()
    {
        var diags = await AnalyzerTestHarness.AnalyzeAsync(Source, new VerifiableSmtAnalyzer());

        Assert.DoesNotContain(diags, d => d.Id == "VER104");
        Assert.Contains(diags, d => d.Id == "VER102");
    }
}
