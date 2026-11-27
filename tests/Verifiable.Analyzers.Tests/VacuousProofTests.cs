using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Verifiable.Analyzers.Smt;
using Xunit;

namespace Verifiable.Analyzers.Tests;

/// <summary>
/// If a method's premises contradict each other, Z3 "proves" every goal, true or false. VER105 rejects
/// those proofs; dead branches (a contradiction on one path only) are normal code and stay quiet.
/// </summary>
public class VacuousProofTests
{
    private static async Task<Diagnostic[]> AnalyzeAsync(string source) =>
        (await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableSmtAnalyzer()))
            .Where(d => d.Id.StartsWith("VER", System.StringComparison.Ordinal))
            .ToArray();

    [Fact]
    public async Task ContradictoryRequires_AreRejected_AndNamed()
    {
        var diags = await AnalyzeAsync("""
            using Verifiable.Contracts;

            [Verifiable]
            public static class Ledger
            {
                public static decimal Withdraw(decimal balance, decimal amount)
                {
                    Spec.Requires(amount > 0m, "Withdrawals move a positive amount.");
                    Spec.Requires(amount < 0m, "Typo: should have been amount <= balance.");
                    Spec.Ensures<decimal>(r => r >= 0m, "Balances never go negative.");
                    return balance - amount; // wrong, yet "proved" because no input satisfies the Requires
                }
            }
            """);

        var vacuous = Assert.Single(diags, d => d.Id == "VER105");
        Assert.Equal(DiagnosticSeverity.Error, vacuous.Severity);
        var message = vacuous.GetMessage();
        Assert.Contains("Ledger.Withdraw", message);
        Assert.Contains("amount > 0m", message);
        Assert.Contains("amount < 0m", message);
        Assert.DoesNotContain(diags, d => d.Id == "VER102");
    }

    [Fact]
    public async Task UnsatisfiableTypeAliasParameter_IsRejected()
    {
        var diags = await AnalyzeAsync("""
            using Verifiable.Contracts;

            [TypeAlias<int>("Percentages are between 0 and 100.")]
            public readonly partial record struct Percent
            {
                public static bool IsValid(int value) => value > 100 && value < 0;
            }

            [Verifiable]
            public static class Discounts
            {
                public static int Apply(int price, Percent off)
                {
                    Spec.Ensures<int>(r => r == price, "Discounts never change the price.");
                    return price - off.Value;
                }
            }
            """);

        Assert.Contains(diags, d => d.Id == "VER105" && d.GetMessage().Contains("Discounts.Apply"));
    }

    [Fact]
    public async Task DeadBranch_IsNotReported()
    {
        var diags = await AnalyzeAsync("""
            using Verifiable.Contracts;

            [Verifiable]
            public static class Grades
            {
                public static int Clamp(int score)
                {
                    Spec.Requires(score >= 0 && score <= 100, "Scores are percentages.");
                    Spec.Ensures<int>(r => r >= 0, "Clamped scores are never negative.");
                    if (score < 0 && score > 100) return -1; // unreachable, but not a contradiction in the premises
                    return score;
                }
            }
            """);

        Assert.DoesNotContain(diags, d => d.Id is "VER105" or "VER102");
    }

    [Fact]
    public async Task ConsistentPremises_AreNotReported()
    {
        var diags = await AnalyzeAsync("""
            using Verifiable.Contracts;

            [Verifiable]
            public static class Ledger
            {
                public static decimal Withdraw(decimal balance, decimal amount)
                {
                    Spec.Requires(amount > 0m && amount <= balance, "Withdrawals are positive and covered.");
                    Spec.Ensures<decimal>(r => r >= 0m, "Balances never go negative.");
                    return balance - amount;
                }
            }
            """);

        Assert.DoesNotContain(diags, d => d.Id is "VER105" or "VER102");
    }

    [Fact]
    public async Task RefutedGoal_StillReportsTheCounterexample_NotVacuity()
    {
        var diags = await AnalyzeAsync("""
            using Verifiable.Contracts;

            [Verifiable]
            public static class Ledger
            {
                public static decimal Withdraw(decimal balance, decimal amount)
                {
                    Spec.Requires(amount > 0m, "Withdrawals move a positive amount.");
                    Spec.Ensures<decimal>(r => r >= 0m, "Balances never go negative.");
                    return balance - amount;
                }
            }
            """);

        Assert.Contains(diags, d => d.Id == "VER102");
        Assert.DoesNotContain(diags, d => d.Id == "VER105");
    }
}
