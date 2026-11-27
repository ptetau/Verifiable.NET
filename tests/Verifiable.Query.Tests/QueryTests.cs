using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using GalaxyCheck;
using GalaxyCheck.Gens;
using Xunit;

namespace Verifiable.Query.Tests;

public sealed class QueryTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Verifiable.NET.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate Verifiable.NET.sln from test output directory.");
    }

    private static (int ExitCode, string Stdout, string Stderr) RunCli(string[] args, string? stdin = null)
    {
        using var outWriter = new StringWriter();
        using var errWriter = new StringWriter();
        var exitCode = Program.Run(args, stdin is null ? null : () => stdin, outWriter, errWriter);
        return (exitCode, outWriter.ToString(), errWriter.ToString());
    }

    [Fact]
    public void AccountingApp_PostTransfer_CreditsEqualDebits_HoldsWithUnsatCoreReasons()
    {
        var repo = FindRepoRoot();
        var csproj = Path.Combine(repo, "samples", "AccountingApp", "AccountingApp.Shared", "AccountingApp.Shared.csproj");

        var (code, stdout, stderr) = RunCli([
            "ask",
            "--project", csproj,
            "--target", "LedgerEngine.PostTransfer",
            "--assert", "result.State.TotalDebits == result.State.TotalCredits",
            "--because", "Double-entry transfers must preserve ledger balance."
        ]);

        Assert.Equal(0, code);
        Assert.True(string.IsNullOrWhiteSpace(stderr), $"Unexpected stderr: {stderr}");
        Assert.Contains("-- QUERY HOLDS", stdout);
        Assert.DoesNotContain("VACUOUSLY", stdout);
        Assert.Contains("Why it holds (from Z3 proof core):", stdout);
        Assert.Contains("LedgerState.IsBalanced", stdout);
        Assert.Contains("total debits must always equal total credits", stdout);
    }

    [Fact]
    public void AccountingApp_PostTransfer_TotalDebitsStrictlyGrows_RefutedWithoutAssume_ProvedWhenSuccessful()
    {
        var repo = FindRepoRoot();
        var csproj = Path.Combine(repo, "samples", "AccountingApp", "AccountingApp.Shared", "AccountingApp.Shared.csproj");

        var unassumed = RunCli([
            "ask",
            "--project", csproj,
            "--target", "LedgerEngine.PostTransfer",
            "--assert", "result.State.TotalDebits > state.TotalDebits"
        ]);

        Assert.Equal(1, unassumed.ExitCode);
        Assert.Contains("-- QUERY REFUTED", unassumed.Stdout);
        Assert.Contains("Counterexample", unassumed.Stdout);

        var assumed = RunCli([
            "ask",
            "--project", csproj,
            "--target", "LedgerEngine.PostTransfer",
            "--assume", "result.IsSuccess",
            "--assert", "result.State.TotalDebits > state.TotalDebits"
        ]);

        Assert.Equal(0, assumed.ExitCode);
        Assert.Contains("-- QUERY HOLDS", assumed.Stdout);
        Assert.DoesNotContain("VACUOUSLY", assumed.Stdout);
        Assert.Contains("PositiveAmount.IsValid", assumed.Stdout);
    }

    [Fact]
    public void AccountingApp_CheckInvariant_LedgerStateBalanced_ProvesAllTransitions()
    {
        var repo = FindRepoRoot();
        var csproj = Path.Combine(repo, "samples", "AccountingApp", "AccountingApp.Shared", "AccountingApp.Shared.csproj");

        var (code, stdout, stderr) = RunCli([
            "check-invariant",
            "--project", csproj,
            "--type", "LedgerState",
            "--expr", "s => s.TotalDebits == s.TotalCredits",
            "--format", "json"
        ]);

        Assert.Equal(0, code);
        Assert.True(string.IsNullOrWhiteSpace(stderr), $"Unexpected stderr: {stderr}");

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        Assert.Equal("proved", root.GetProperty("status").GetString());
        var methods = root.GetProperty("results").EnumerateArray()
            .Select(r => r.GetProperty("method").GetString())
            .ToList();

        Assert.Contains("LedgerState.CreateEmpty", methods);
        Assert.Contains("LedgerEngine.PostCheckedAssetTransfer", methods);
        Assert.Contains("LedgerEngine.OpenAccount", methods);
        Assert.Contains("LedgerEngine.PostTransfer", methods);
        Assert.Contains("LedgerEngine.PostBalancedEntry", methods);
        Assert.Contains("LedgerEngine.ReverseEntry", methods);
        Assert.All(root.GetProperty("results").EnumerateArray(), r =>
        {
            Assert.Equal("proved", r.GetProperty("status").GetString());
            Assert.False(r.GetProperty("vacuous").GetBoolean());
        });
    }

    [Fact]
    public void TodoApp_CheckInvariant_TodoBoardConsistency_ProvesAllTransitions()
    {
        var repo = FindRepoRoot();
        var csproj = Path.Combine(repo, "samples", "TodoApp", "TodoApp.Shared", "TodoApp.Shared.csproj");

        var (code, stdout, stderr) = RunCli([
            "check-invariant",
            "--project", csproj,
            "--type", "TodoBoard",
            "--expr", "b => b.Items.Count == b.ActiveCount + b.CompletedCount + b.DeletedCount",
            "--format", "json"
        ]);

        Assert.Equal(0, code);
        Assert.True(string.IsNullOrWhiteSpace(stderr), $"Unexpected stderr: {stderr}");

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        Assert.Equal("proved", root.GetProperty("status").GetString());
        var methods = root.GetProperty("results").EnumerateArray()
            .Select(r => r.GetProperty("method").GetString())
            .ToList();

        Assert.Contains("TodoBoard.CreateEmpty", methods);
        Assert.Contains("TodoEngine.Add", methods);
        Assert.Contains("TodoEngine.Complete", methods);
        Assert.Contains("TodoEngine.Delete", methods);
    }

    [Fact]
    public void StdinBuggyCode_RefutesWithElmCounterexampleAndAgentJson()
    {
        const string source = """
            using Verifiable.Contracts;

            namespace DemoLedger;

            [Verifiable]
            public readonly record struct SimpleLedger(decimal Debits, decimal Credits);

            [Verifiable]
            public static class BuggyOps
            {
                public static SimpleLedger BuggyTransfer(SimpleLedger ledger, decimal amount)
                {
                    Spec.Requires(amount > 0m, "Transfer amount must be positive.");
                    // Bug: only debits increase; forgot credits!
                    return ledger with { Debits = ledger.Debits + amount };
                }
            }
            """;

        var oldEnv = Environment.GetEnvironmentVariable("VERIFIABLE_AGENT");
        try
        {
            Environment.SetEnvironmentVariable("VERIFIABLE_AGENT", "1");
            var (code, stdout, stderr) = RunCli([
                "ask",
                "--stdin",
                "--target", "BuggyOps.BuggyTransfer",
                "--assume", "ledger.Debits == ledger.Credits",
                "--assert", "result.Debits == result.Credits"
            ], source);

            Assert.Equal(1, code);
            Assert.True(string.IsNullOrWhiteSpace(stderr));
            Assert.Contains("-- QUERY REFUTED", stdout);
            Assert.Contains("Counterexample", stdout);
            Assert.Contains("amount =", stdout);
            Assert.Contains("Fix:", stdout);

            var match = Regex.Match(stdout, @"<verifiable-query-result>(?<json>\{.*\})</verifiable-query-result>");
            Assert.True(match.Success, $"Expected <verifiable-query-result> in stdout:\n{stdout}");

            using var doc = JsonDocument.Parse(match.Groups["json"].Value);
            var root = doc.RootElement;
            Assert.Equal("refuted", root.GetProperty("status").GetString());
            Assert.Equal(1, root.GetProperty("exitCode").GetInt32());
            var ce = root.GetProperty("results")[0].GetProperty("counterexamples")[0].GetProperty("values");
            Assert.True(ce.TryGetProperty("amount", out _));
        }
        finally
        {
            Environment.SetEnvironmentVariable("VERIFIABLE_AGENT", oldEnv);
        }
    }

    [Theory]
    [InlineData("Quantity", 0)]
    [InlineData("Unknown", 3)]
    public void GeneratedProjectionIsAvailableToQueries(string sourceMember, int expected)
    {
        var source = $$"""
            using Verifiable.Contracts;
            namespace Orders;
            [MapTo(typeof(State))]
            public sealed class OrderDto { public int Id { get; set; } public int {{sourceMember}} { get; set; } }
            public readonly record struct State(int Quantity, int Id);
            [Verifiable] public static class Rules
            {
                public static State Read(OrderDto dto) => dto.ToState();
            }
            """;
        var (code, stdout, stderr) = RunCli(["ask", "--stdin", "--target", "Rules.Read",
            "--assert", "result.Quantity == dto.Quantity && result.Id == dto.Id"], source);
        Assert.True(code == expected, stdout + stderr);
        if (expected == 3) Assert.Contains("VER203", stdout + stderr);
    }

    [Theory]
    [InlineData("x >= 0", 0)]
    [InlineData("true", 2)]
    [InlineData("Rules.Checked(x) >= 0", 2)]
    public void QueryHelperPreconditionCannotBeProvedByTheAssumptionItGuards(string assume, int expected)
    {
        const string source = """
            using Verifiable.Contracts;
            [Verifiable] public static class Rules
            {
                public static int Checked(int x) { Spec.Requires(x >= 0); return x; }
                public static int Keep(int x) => x;
            }
            """;
        var (code, stdout, stderr) = RunCli(["ask", "--stdin", "--target", "Rules.Keep",
            "--assert", "result == Rules.Checked(x)", "--assume", assume], source);
        Assert.True(code == expected, stdout + stderr);
        if (expected == 2) Assert.Contains("call precondition", stdout + stderr);
    }

    [Fact]
    public void ContradictoryAssume_DetectsVacuousProofAndReportsJsonFlag()
    {
        const string source = """
            using Verifiable.Contracts;

            namespace VacuousDemo;

            [Verifiable]
            public static class Calc
            {
                public static int Step(int x) => x + 1;
            }
            """;

        var (code, stdout, _) = RunCli([
            "ask",
            "--stdin",
            "--target", "Calc.Step",
            "--assume", "x > 10 && x < 5",
            "--assert", "result == 999",
            "--format", "json"
        ], source);

        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(stdout);
        var res = doc.RootElement.GetProperty("results")[0];
        Assert.Equal("proved", res.GetProperty("status").GetString());
        Assert.True(res.GetProperty("vacuous").GetBoolean());
    }

    [Fact]
    public void BadExpressionTypo_ReturnsExit3WithHelpfulAvailableProperties()
    {
        const string source = """
            using Verifiable.Contracts;

            namespace TypoDemo;

            [Verifiable]
            public readonly record struct Account(decimal Balance);

            [Verifiable]
            public static class Bank
            {
                public static Account Zero(Account acc) => acc with { Balance = 0m };
            }
            """;

        var (code, _, stderr) = RunCli([
            "ask",
            "--stdin",
            "--target", "Bank.Zero",
            "--assert", "result.MissingField == 0m"
        ], source);

        Assert.Equal(3, code);
        Assert.Contains("-- QUERY EXPRESSION ERROR", stderr);
        Assert.Contains("Balance", stderr);
    }

    [Fact]
    public void ListCommand_OnAccountingApp_EmitsTypesInvariantsAndMethods()
    {
        var repo = FindRepoRoot();
        var csproj = Path.Combine(repo, "samples", "AccountingApp", "AccountingApp.Shared", "AccountingApp.Shared.csproj");

        var (code, stdout, stderr) = RunCli([
            "list",
            "--project", csproj,
            "--format", "json"
        ]);

        Assert.Equal(0, code);
        Assert.True(string.IsNullOrWhiteSpace(stderr));

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        var typeNames = root.GetProperty("constrainedTypes").EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToList();
        var invNames = root.GetProperty("invariants").EnumerateArray().Select(e => $"{e.GetProperty("type").GetString()}.{e.GetProperty("member").GetString()}").ToList();
        var methodNames = root.GetProperty("methods").EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToList();

        Assert.Contains("PositiveAmount", typeNames);
        Assert.Contains("LedgerState.IsBalanced", invNames);
        Assert.Contains("LedgerEngine.PostTransfer", methodNames);
    }

    [Property(Iterations = 12)]
    public Property QueryEngine_SoundnessAcrossGeneratedLinearClaims() =>
        (from threshold in Gen.Int32().Between(1, 50)
         from delta in Gen.Int32().Between(1, 20)
         select (threshold, delta)).ForAll(p =>
        {
            var source = $$"""
                using Verifiable.Contracts;

                namespace PropQuery;

                [Verifiable]
                public static class LinearMath
                {
                    public static int AddOffset(int x)
                    {
                        Spec.Requires(x >= {{p.threshold}}, "Input meets minimum threshold.");
                        return x + {{p.delta}};
                    }
                }
                """;

            var loaded = WorkspaceLoader.Load(null, Array.Empty<string>(), source);
            if (loaded.Compilation is null || loaded.CompilationErrors.Count > 0)
            {
                return false;
            }

            var trueReport = QueryEngine.Ask(
                loaded.Compilation,
                "LinearMath.AddOffset",
                false,
                $"result >= {p.threshold + p.delta}",
                null,
                null,
                null,
                5000);

            var falseReport = QueryEngine.Ask(
                loaded.Compilation,
                "LinearMath.AddOffset",
                false,
                $"result >= {p.threshold + p.delta + 1}",
                null,
                null,
                null,
                5000);

            return trueReport.Status == QueryOutcomeStatus.Proved
                && trueReport.Results.Count == 1
                && !trueReport.Results[0].Vacuous
                && trueReport.Results[0].Reasons.Any(r => r.Kind == "Requires")
                && falseReport.Status == QueryOutcomeStatus.Refuted
                && falseReport.Results.Count == 1
                && falseReport.Results[0].Counterexamples.Count == 1;
        });

    [Fact]
    public void InventoryPricing_CheckInvariant_PriceBreakdownConsistent_ProvesPriceCart()
    {
        var repo = FindRepoRoot();
        var csproj = Path.Combine(repo, "samples", "InventoryPricing", "InventoryPricing.csproj");

        var (code, stdout, stderr) = RunCli([
            "check-invariant",
            "--project", csproj,
            "--type", "PriceBreakdown",
            "--expr", "p => p.GrandTotalCents == p.SubtotalCents - p.DiscountCents + p.TaxCents && p.DiscountCents <= p.SubtotalCents",
            "--format", "json"
        ]);

        Assert.Equal(0, code);
        Assert.True(string.IsNullOrWhiteSpace(stderr), $"Unexpected stderr: {stderr}");

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        Assert.Equal("proved", root.GetProperty("status").GetString());
        Assert.Contains(
            root.GetProperty("results").EnumerateArray(),
            r => r.GetProperty("method").GetString() == "PricingEngine.PriceCart"
              && r.GetProperty("status").GetString() == "proved");
    }
}
