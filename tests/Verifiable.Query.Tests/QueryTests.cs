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
                        Spec.Requires(x >= {{p.threshold}} && x <= 1000, "Input meets minimum threshold.");
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

    [Fact]
    public void CheckedEnumCastOverflow_MakesQueryInconclusiveInsteadOfProving()
    {
        const string source = """
            using Verifiable.Contracts;
            public enum ByteEnum : byte { A = 1, B = 2 }
            [Verifiable]
            public static class CastProbe
            {
                public static ByteEnum CastChecked(int n) => checked((ByteEnum)n);
            }
            """;

        var (code, stdout, stderr) = RunCli([
            "ask", "--stdin",
            "--target", "CastProbe.CastChecked",
            "--assume", "n == 258",
            "--assert", "result == (ByteEnum)2"
        ], source);

        Assert.Equal(2, code);
        Assert.Contains("Checked cast", stdout + stderr);
    }

    [Fact]
    public void MapToGeneratedProjection_ProvesFieldPreservationInQueryCli()
    {
        const string source = """
            using Verifiable.Contracts;
            namespace MapQueryDemo;

            [MapTo(typeof(OrderState))]
            public class OrderDto
            {
                public int Id { get; set; }
                public decimal Total { get; set; }
            }

            public readonly record struct OrderState(int Id, decimal Total);

            [Verifiable]
            public static class Adapter
            {
                public static OrderState Convert(OrderDto dto) => dto.ToOrderState();
            }
            """;

        var (okCode, okOut, okErr) = RunCli([
            "ask", "--stdin",
            "--target", "Adapter.Convert",
            "--assert", "result.Id == dto.Id && result.Total == dto.Total"
        ], source);
        Assert.True(okCode == 0, okOut + okErr);

        var (badCode, badOut, badErr) = RunCli([
            "ask", "--stdin",
            "--target", "Adapter.Convert",
            "--assert", "result.Total > dto.Total"
        ], source);
        Assert.True(badCode == 1, badOut + badErr);
    }

    [Fact]
    public void ElmTypoSuggestions_RankTargetTypeMemberAndAssignmentMistakes()
    {
        const string source = """
            using Verifiable.Contracts;

            namespace TypoCatalog;

            [Verifiable]
            public readonly record struct AccountState(decimal Balance, int Version);

            [Verifiable]
            public static class AccountOps
            {
                public static AccountState ResetBalance(AccountState state) =>
                    state with { Balance = 0m, Version = state.Version + 1 };
            }
            """;

        // 1. Misspelled --target
        var badTarget = RunCli([
            "ask", "--stdin",
            "--target", "AccountOps.ResetBalnce",
            "--assert", "result.Balance == 0m"
        ], source);
        Assert.Equal(3, badTarget.ExitCode);
        Assert.Contains("Did you mean `AccountOps.ResetBalance`?", badTarget.Stderr);

        // 2. Misspelled --type in check-invariant
        var badType = RunCli([
            "check-invariant", "--stdin",
            "--type", "AccountStat",
            "--expr", "a => a.Balance >= 0m"
        ], source);
        Assert.Equal(3, badType.ExitCode);
        Assert.Contains("Did you mean `AccountState`?", badType.Stderr);

        // 3. Misspelled member in --assert
        var badMember = RunCli([
            "ask", "--stdin",
            "--target", "AccountOps.ResetBalance",
            "--assert", "result.Balnce == 0m"
        ], source);
        Assert.Equal(3, badMember.ExitCode);
        Assert.Contains("I couldn't find `Balnce` on `AccountState`", badMember.Stderr);
        Assert.Contains("Did you mean `Balance`?", badMember.Stderr);

        // 4. Assignment '=' instead of '=='
        var badAssign = RunCli([
            "ask", "--stdin",
            "--target", "AccountOps.ResetBalance",
            "--assert", "result.Balance = 0m"
        ], source);
        Assert.Equal(3, badAssign.ExitCode);
        Assert.Contains("instead of `==`", badAssign.Stderr);
    }

    [Fact]
    public void RefutedCompoundQuery_PinpointsFailingClauseWithLeftAndRightValues()
    {
        const string source = """
            using Verifiable.Contracts;

            namespace ClauseCatalog;

            [Verifiable]
            public readonly record struct PairState(int First, int Second);

            [Verifiable]
            public static class PairOps
            {
                public static PairState BumpFirstOnly(PairState p)
                {
                    Spec.Requires(p.First >= -1000 && p.First <= 1000 && p.Second >= -1000 && p.Second <= 1000);
                    return p with { First = p.First + 1 };
                }
            }
            """;

        var (code, stdout, stderr) = RunCli([
            "ask", "--stdin",
            "--target", "PairOps.BumpFirstOnly",
            "--assert", "result.First == p.First + 1 && result.Second == p.Second + 1"
        ], source);

        Assert.Equal(1, code);
        Assert.True(string.IsNullOrWhiteSpace(stderr), $"Unexpected stderr: {stderr}");
        Assert.Contains("-- QUERY REFUTED", stdout);
        Assert.Contains("Failed clause (2 of 2):", stdout);
        Assert.Contains("result.Second == p.Second + 1", stdout);
        Assert.Contains("Left  (result.Second):", stdout);
        Assert.Contains("Right (p.Second + 1):", stdout);
    }

    [Fact]
    public void ElmCSharpDiagnosticRewriter_ImprovesRoslynErrorsWithoutCustomCompiler()
    {
        // 1. Generic type argument diff (Result<int, int> vs Result<int, string>)
        const string genericMismatch = """
            using Verifiable.Contracts;
            [Verifiable]
            public static class GenericDemo
            {
                public static Result<int, string> Bad() => Result<int, int>.Ok(1);
            }
            """;

        var genRes = RunCli(["list", "--stdin"], genericMismatch);
        Assert.Equal(3, genRes.ExitCode);
        Assert.Contains("-- TYPE MISMATCH", genRes.Stderr);
        Assert.Contains("2nd type argument", genRes.Stderr);
        Assert.Contains("Actual:   `int`", genRes.Stderr);
        Assert.Contains("Expected: `string`", genRes.Stderr);

        // 2. Classic C# double-vs-decimal literal hint (C#'s version of Elm's IntFloat)
        const string decimalMismatch = """
            using Verifiable.Contracts;
            [Verifiable]
            public static class MoneyDemo
            {
                public static decimal Tax() => 0.15;
            }
            """;

        var decRes = RunCli(["list", "--stdin"], decimalMismatch);
        Assert.Equal(3, decRes.ExitCode);
        Assert.Contains("-- TYPE MISMATCH", decRes.Stderr);
        Assert.Contains("0.15m", decRes.Stderr);

        // 3. C# member typo suggestion via Roslyn SemanticModel + ElmSuggest
        const string memberTypo = """
            using Verifiable.Contracts;
            [Verifiable]
            public readonly record struct Wallet(decimal Balance);
            [Verifiable]
            public static class WalletOps
            {
                public static decimal Read(Wallet w) => w.Balnce;
            }
            """;

        var memRes = RunCli(["list", "--stdin"], memberTypo);
        Assert.Equal(3, memRes.ExitCode);
        Assert.Contains("-- MISSING MEMBER", memRes.Stderr);
        Assert.Contains("Did you mean `Balance`?", memRes.Stderr);

        // 4. Unknown CLI subcommand typo suggestion
        var cmdRes = RunCli(["check-invarint"]);
        Assert.Equal(3, cmdRes.ExitCode);
        Assert.Contains("Did you mean `check-invariant`?", cmdRes.Stderr);
    }

    [Fact]
    public void ElmCSharpDiagnosticRewriter_CoversMissingImportsArityTaskNullableOperatorsAndSwitchArms()
    {
        // 1. Missing `using` discovery across referenced assemblies
        const string missingImport = """
            using Verifiable.Contracts;
            [Verifiable]
            public static class ImportDemo
            {
                public static int Count(ImmutableList<int> xs) => xs.Count;
            }
            """;
        var impRes = RunCli(["list", "--stdin"], missingImport);
        Assert.Equal(3, impRes.ExitCode);
        Assert.Contains("-- MISSING IMPORT", impRes.Stderr);
        Assert.Contains("using System.Collections.Immutable;", impRes.Stderr);

        // 1b. Missing `using` discovery on CS0103 generic static member access (e.g., `ImmutableList<int>.Empty`)
        const string missingGenericStaticImport = """
            public static class GenericStaticImportDemo
            {
                public static void Make()
                {
                    var items = ImmutableList<int>.Empty;
                }
            }
            """;
        var impStaticRes = RunCli(["check", "--stdin", "--color", "never"], missingGenericStaticImport);
        Assert.Equal(1, impStaticRes.ExitCode);
        Assert.Contains("-- MISSING IMPORT", impStaticRes.Stderr);
        Assert.Contains("ImmutableList<int>", impStaticRes.Stderr);
        Assert.Contains("using System.Collections.Immutable;", impStaticRes.Stderr);

        // 2. Arity mismatch (Elm's TOO MANY ARGS / MISSING ARGS)
        const string arityMismatch = """
            using Verifiable.Contracts;
            [Verifiable]
            public static class ArityDemo
            {
                public static int Add(int a, int b) => a + b;
                public static int Bad() => Add(1, 2, 3);
            }
            """;
        var arityRes = RunCli(["list", "--stdin"], arityMismatch);
        Assert.Equal(3, arityRes.ExitCode);
        Assert.Contains("-- TOO MANY ARGS", arityRes.Stderr);
        Assert.Contains("expects 2 arguments, but it got 3 instead", arityRes.Stderr);
        Assert.Contains("Add(int a, int b) -> int", arityRes.Stderr);

        // 3. Task<T> vs T ("Did you forget await?")
        const string taskMismatch = """
            using System.Threading.Tasks;
            public static class AsyncDemo
            {
                public static Task<int> Fetch() => Task.FromResult(42);
                public static int Use() => Fetch();
            }
            """;
        var taskRes = RunCli(["list", "--stdin"], taskMismatch);
        Assert.Equal(3, taskRes.ExitCode);
        Assert.Contains("-- TYPE MISMATCH", taskRes.Stderr);
        Assert.Contains("Did you forget `await`", taskRes.Stderr);

        // 4. Nullable<T> vs T (Elm's AnythingFromMaybe)
        const string nullableMismatch = """
            public static class NullDemo
            {
                public static int Unwrap(int? x) => x;
            }
            """;
        var nullRes = RunCli(["list", "--stdin"], nullableMismatch);
        Assert.Equal(3, nullRes.ExitCode);
        Assert.Contains("-- TYPE MISMATCH", nullRes.Stderr);
        Assert.Contains("can be `null`", nullRes.Stderr);
        Assert.Contains("??", nullRes.Stderr);

        // 5. Operator mismatch (decimal + double)
        const string opMismatch = """
            public static class OpDemo
            {
                public static decimal Sum(decimal a) => a + 0.5;
            }
            """;
        var opRes = RunCli(["list", "--stdin"], opMismatch);
        Assert.Equal(3, opRes.ExitCode);
        Assert.Contains("-- OPERATOR MISMATCH", opRes.Stderr);
        Assert.Contains("0.5m", opRes.Stderr);

        // 6. Switch expression arm mismatch (CS8506)
        const string switchMismatch = """
            public static class SwitchDemo
            {
                public static object Pick(int x)
                {
                    var chosen = x switch
                    {
                        0 => 1,
                        _ => "other"
                    };
                    return chosen;
                }
            }
            """;
        var swRes = RunCli(["list", "--stdin"], switchMismatch);
        Assert.Equal(3, swRes.ExitCode);
        Assert.Contains("-- BRANCH TYPE MISMATCH", swRes.Stderr);
        Assert.Contains("Arm 1: `int`", swRes.Stderr);
        Assert.Contains("Arm 2: `string`", swRes.Stderr);
    }

    [Fact]
    public void CheckCommand_RunsAnalyzersSuppressorAndAnsiColorOutput()
    {
        // Clean code whose CS8602 is proved safe and suppressed by Z3
        const string cleanWithNullProof = """
            using Verifiable.Contracts;

            namespace CheckClean;

            [Verifiable]
            public static class SafeText
            {
                public static int Measure(string? s)
                {
                    Spec.Requires(!string.IsNullOrEmpty(s), "Input text must be non-empty.");
                    return s.Length;
                }
            }
            """;

        var ok = RunCli(["check", "--stdin", "--color", "always"], cleanWithNullProof);
        Assert.Equal(0, ok.ExitCode);
        Assert.Contains("-- CHECK PASSED", ok.Stdout);
        Assert.Contains("automatically suppressed after Z3 proved non-null", ok.Stdout);
        Assert.Contains("\x1b[36m", ok.Stdout, StringComparison.Ordinal); // Cyan ANSI banner

        // Broken code with VER102 failure -> full multi-line Elm output with ANSI colors
        const string broken = """
            using Verifiable.Contracts;

            namespace CheckBroken;

            [Verifiable]
            public static class BrokenVault
            {
                public static decimal Withdraw(decimal balance, decimal amount)
                {
                    Spec.Requires(amount > 0m);
                    Spec.Ensures<decimal>(r => r >= 0m, "No overdrafts.");
                    return balance - amount;
                }
            }
            """;

        var failColor = RunCli(["check", "--stdin", "--color", "always"], broken);
        Assert.Equal(1, failColor.ExitCode);
        Assert.Contains("-- PROOF FAILED", failColor.Stderr);
        Assert.Contains("No overdrafts.", failColor.Stderr);
        Assert.Contains("\x1b[36m", failColor.Stderr, StringComparison.Ordinal); // Cyan banner
        Assert.Contains("\x1b[31m", failColor.Stderr, StringComparison.Ordinal); // Red carets

        var failJson = RunCli(["check", "--stdin", "--format", "json"], broken);
        Assert.Equal(1, failJson.ExitCode);
        Assert.DoesNotContain("\x1b[", failJson.Stdout, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(failJson.Stdout);
        Assert.Equal(1, doc.RootElement.GetProperty("errors").GetInt32());
        Assert.Equal("VER102", doc.RootElement.GetProperty("diagnostics")[0].GetProperty("id").GetString());
    }

    [Fact]
    public void CheckSarif_PresentsMultiLineVerAndEnrichedCsDiagnosticsWithColor()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"verifiable-sarif-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var csPath = Path.Combine(tempDir, "Sample.cs");
            File.WriteAllText(csPath, """
                using Verifiable.Contracts;
                [Verifiable]
                public static class Sample
                {
                    public static decimal Tax() => 0.15;
                }
                """);

            var sarifPath = Path.Combine(tempDir, "build.sarif");
            var sarifJson = $$"""
                {
                  "version": "2.1.0",
                  "runs": [
                    {
                      "results": [
                        {
                          "ruleId": "VER102",
                          "level": "error",
                          "message": {
                            "text": "Postcondition fails; x = 0\n-- PROOF FAILED ------------------------------------------- Sample.cs\n\nZ3 found inputs that break a postcondition.\n\n  |\n5 |     return x;\n  |     ^^^^^^^^^\n\nFix: Add a precondition."
                          },
                          "locations": [
                            {
                              "physicalLocation": {
                                "artifactLocation": { "uri": "{{new Uri(csPath).AbsoluteUri}}" },
                                "region": { "startLine": 5, "startColumn": 5, "endLine": 5, "endColumn": 14 }
                              }
                            }
                          ]
                        },
                        {
                          "ruleId": "CS0664",
                          "level": "error",
                          "message": {
                            "text": "Literal of type double cannot be implicitly converted to type 'decimal'"
                          },
                          "locations": [
                            {
                              "physicalLocation": {
                                "artifactLocation": { "uri": "{{new Uri(csPath).AbsoluteUri}}" },
                                "region": { "startLine": 5, "startColumn": 36, "endLine": 5, "endColumn": 40 }
                              }
                            }
                          ]
                        }
                      ]
                    }
                  ]
                }
                """;
            File.WriteAllText(sarifPath, sarifJson);

            var res = RunCli(["check", "--sarif", sarifPath, "--file", csPath, "--color", "always"]);
            Assert.Equal(1, res.ExitCode);
            Assert.Contains("-- PROOF FAILED", res.Stderr);
            Assert.Contains("-- TYPE MISMATCH", res.Stderr);
            Assert.Contains("0.15m", res.Stderr);
            Assert.Contains("\x1b[36m", res.Stderr, StringComparison.Ordinal);
            Assert.Contains("\x1b[31m", res.Stderr, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Clauses_ReportsConstructsPerClause_AndGaps()
    {
        var dir = Path.Combine(Path.GetTempPath(), "verifiable-clauses-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "Bookings.cs"), """
                using Verifiable.Contracts;

                namespace Bookings;

                [Verifiable]
                public static class Schedule
                {
                    public static int Length(int start, int end)
                    {
                        Spec.Requires(start >= 0 && end <= 1000 && start <= end, "Times are in range.", clause: "C1");
                        Spec.Ensures<int>(r => r >= 0, "A booking ends after it starts", clause: "C2");
                        return end - start;
                    }

                    public static int Twice(int x)
                    {
                        Spec.Requires(x >= 0 && x < 1000, "Small.", clause: "C9");
                        Spec.Ensures<int>(r => r == x * 2, "Doubles.");
                        return x * 2;
                    }
                }
                """);
            File.WriteAllText(Path.Combine(dir, "verifiable.clauses.json"), """
                { "clauses": [
                  { "id": "C1", "text": "Times are in range." },
                  { "id": "C2", "text": "A booking ends after it starts." },
                  { "id": "C3", "text": "Never double booked." }
                ] }
                """);

            var (code, stdout, stderr) = RunCli(["clauses", dir]);
            Assert.Equal(1, code);
            Assert.True(string.IsNullOrWhiteSpace(stderr), stderr);

            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            var clauses = root.GetProperty("clauses").EnumerateArray().ToList();
            Assert.Equal(["C1", "C2", "C3"], clauses.Select(c => c.GetProperty("id").GetString()).ToArray());
            Assert.Equal("Never double booked.", clauses[2].GetProperty("text").GetString());
            Assert.Empty(clauses[2].GetProperty("constructs").EnumerateArray());

            var c2 = Assert.Single(clauses[1].GetProperty("constructs").EnumerateArray());
            Assert.Equal("ensures", c2.GetProperty("kind").GetString());
            Assert.Equal("Bookings.Schedule.Length(int, int)", c2.GetProperty("symbol").GetString());
            Assert.Equal("A booking ends after it starts", c2.GetProperty("because").GetString());
            Assert.Equal("Bookings.cs", c2.GetProperty("file").GetString());
            Assert.Equal(11, c2.GetProperty("line").GetInt32());

            var unknown = Assert.Single(root.GetProperty("unknownClauses").EnumerateArray());
            Assert.Equal("C9", unknown.GetProperty("id").GetString());
            Assert.Equal(["C3"], root.GetProperty("uncoveredClauses").EnumerateArray().Select(e => e.GetString()).ToArray());
            var without = Assert.Single(root.GetProperty("constructsWithoutClause").EnumerateArray());
            Assert.Equal("Bookings.Schedule.Twice(int)", without.GetProperty("symbol").GetString());

            var outFile = Path.Combine(dir, "report.json");
            var (code2, stdout2, _) = RunCli(["clauses", dir, "--out", outFile]);
            Assert.Equal(1, code2);
            Assert.True(string.IsNullOrWhiteSpace(stdout2));
            Assert.Equal(stdout.Trim(), File.ReadAllText(outFile).Trim());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Clauses_MalformedManifest_IsAnError()
    {
        var dir = Path.Combine(Path.GetTempPath(), "verifiable-clauses-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "A.cs"), "public class A { }");
            File.WriteAllText(Path.Combine(dir, "verifiable.clauses.json"), """{ "clauses": [ { "id": "C1" }, { "id": "C1" } ] }""");
            var (code, _, stderr) = RunCli(["clauses", dir]);
            Assert.Equal(3, code);
            Assert.Contains("more than once", stderr);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
