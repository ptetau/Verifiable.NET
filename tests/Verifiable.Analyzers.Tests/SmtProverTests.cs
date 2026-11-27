using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using GalaxyCheck;
using GalaxyCheck.Gens;
using Verifiable.Analyzers.Smt;
using Verifiable.Core;
using Xunit;

namespace Verifiable.Analyzers.Tests;

public class SmtProverTests
{
    [Fact]
    public void Z3Binary_IsDiscoveredAndResponsive()
    {
        var path = Z3SolverRunner.DiscoverZ3Path();
        Assert.NotNull(path);

        var res = Z3SolverRunner.Execute("(declare-const x Int)\n(assert (> x 5))\n(assert (not (> x 3)))");
        Assert.Equal(SmtStatus.Unsat, res.Status);
    }

    [Fact]
    public async Task OccurrenceTyping_PathConditionProvesCalleePrecondition()
    {
        const string source = """
            using Verifiable.Contracts;

            namespace SmtSamples;

            [Verifiable]
            public static class OccurrenceExample
            {
                public static int DoubleBounded(int x)
                {
                    Spec.Requires(x > 5 && x <= 100);
                    Spec.Ensures<int>(result => result >= 12);
                    return x * 2;
                }

                public static int Caller(int requested)
                {
                    Spec.Ensures<int>(result => result >= 0);
                    if (requested > 5 && requested <= 100)
                    {
                        return DoubleBounded(requested);
                    }
                    return 0;
                }
            }
            """;

        var diags = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableSmtAnalyzer());
        Assert.DoesNotContain(diags, d => d.Id == "AD0001");
        Assert.DoesNotContain(diags, d => d.Id.StartsWith("VER1"));
    }

    [Fact]
    public async Task FailingPostcondition_ProducesVER102WithConcreteCounterexample()
    {
        const string source = """
            using Verifiable.Contracts;

            namespace SmtSamples;

            [Verifiable]
            public static class BrokenMath
            {
                public static int AddFive(int x)
                {
                    Spec.Requires(x >= 0);
                    Spec.Ensures<int>(result => result >= 10);
                    return x + 5;
                }
            }
            """;

        var diags = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableSmtAnalyzer());
        var err = Assert.Single(diags.Where(d => d.Id == "VER102"));
        var msg = err.GetMessage();
        Assert.Contains("x =", msg);
        Assert.Contains("Fix:", msg);
    }

    [Fact]
    public async Task ExactDecimalArithmetic_ConservesCentsAndChecksOverdraft()
    {
        const string validSource = """
            using Verifiable.Contracts;

            namespace SmtSamples;

            [Verifiable]
            public static class LedgerCheck
            {
                public static decimal ExactDecimalSum()
                {
                    Spec.Ensures<decimal>(result => result == 0.3m);
                    return 0.1m + 0.2m;
                }

                public static decimal Withdraw(decimal fromBal, decimal amount)
                {
                    Spec.Requires(fromBal >= amount && amount > 0m);
                    Spec.Ensures<decimal>(result => result >= 0m);
                    return fromBal - amount;
                }
            }
            """;

        var validDiags = await AnalyzerTestHarness.AnalyzeAsync(validSource, new VerifiableSmtAnalyzer());
        Assert.DoesNotContain(validDiags, d => d.Id.StartsWith("VER1"));

        const string invalidSource = """
            using Verifiable.Contracts;

            namespace SmtSamples;

            [Verifiable]
            public static class BrokenLedger
            {
                public static decimal UnguardedWithdraw(decimal fromBal, decimal amount)
                {
                    Spec.Requires(amount > 0m);
                    Spec.Ensures<decimal>(result => result >= 0m);
                    return fromBal - amount;
                }
            }
            """;

        var badDiags = await AnalyzerTestHarness.AnalyzeAsync(invalidSource, new VerifiableSmtAnalyzer());
        Assert.Contains(badDiags, d => d.Id == "VER102" && d.GetMessage().Contains("fromBal ="));
    }

    [Fact]
    public async Task StringLengthAndTypeAliasString_AreVerifiedByZ3()
    {
        const string source = """
            using Verifiable.Contracts;

            namespace SmtSamples;

            [TypeAlias<string>]
            public readonly partial record struct TitleText
            {
                public static bool IsValid(string value) => value is { Length: >= 1 and <= 200 };
            }

            [Verifiable]
            public static class Strings
            {
                public static int GetLength(TitleText title)
                {
                    Spec.Ensures<int>(result => result >= 1 && result <= 200);
                    return title.Length;
                }
            }
            """;

        var diags = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableSmtAnalyzer());
        Assert.DoesNotContain(diags, d => d.Id == "AD0001");
        Assert.DoesNotContain(diags, d => d.Id.StartsWith("VER1"));
    }

    [Fact]
    public async Task CheckedArithmetic_DetectsOverflowAndProvesBoundedAddition()
    {
        const string overflowSource = """
            using Verifiable.Contracts;

            namespace SmtSamples;

            [Verifiable, CheckedArithmetic]
            public static class CheckedOps
            {
                public static int UnboundedAdd(int a, int b) => a + b;

                public static int BoundedAdd(int a, int b)
                {
                    Spec.Requires(a >= 0 && a <= 1000 && b >= 0 && b <= 1000);
                    Spec.Ensures<int>(result => result >= 0 && result <= 2000);
                    return a + b;
                }
            }
            """;

        var diags = await AnalyzerTestHarness.AnalyzeAsync(overflowSource, new VerifiableSmtAnalyzer());
        var errors = diags.Where(d => d.Id == "VER102").ToList();
        Assert.Single(errors);
        Assert.Contains("overflow", errors[0].GetMessage(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ImmutableDictionaryAndRecordWith_ProveTodoBoardInvariants()
    {
        const string source = """
            using System.Collections.Immutable;
            using Verifiable.Contracts;

            namespace SmtSamples;

            [TypeAlias<int>]
            public readonly partial record struct ItemId
            {
                public static bool IsValid(int v) => v >= 1;
            }

            [Verifiable]
            public readonly record struct Item(ItemId Id, int Weight);

            [Verifiable]
            public readonly record struct Board(
                ItemId NextId,
                ImmutableDictionary<ItemId, Item> Items,
                int ActiveCount)
            {
                [Invariant]
                public bool IsConsistent() =>
                    NextId >= 1
                    && ActiveCount >= 0
                    && Items.Count == ActiveCount
                    && Items.Count == NextId - 1;
            }

            [Verifiable]
            public static class Engine
            {
                public static Board AddItem(Board board, int weight)
                {
                    Spec.Requires(weight >= 1 && !board.Items.ContainsKey(board.NextId));
                    Spec.Ensures<Board>(result =>
                        result.NextId == board.NextId + 1
                        && result.ActiveCount == board.ActiveCount + 1
                        && result.Items.Count == board.Items.Count + 1);

                    var item = new Item(board.NextId, weight);
                    var nextId = ItemId.From(board.NextId + 1);
                    return board with
                    {
                        NextId = nextId,
                        Items = board.Items.Add(board.NextId, item),
                        ActiveCount = board.ActiveCount + 1
                    };
                }
            }
            """;

        var diags = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableSmtAnalyzer());
        Assert.DoesNotContain(diags, d => d.Id == "AD0001");
        Assert.DoesNotContain(diags, d => d.Id.StartsWith("VER1"));
    }

    [Fact]
    public async Task LoopInvariantAndDecreases_PassWhenInductiveAndSupportMultipleLoops()
    {
        const string validLoops = """
            using Verifiable.Contracts;

            namespace SmtSamples;

            [Verifiable]
            public static class Loops
            {
                public static int SumUpTwice(int n)
                {
                    Spec.Requires(n >= 0);
                    Spec.Ensures<int>(result => result >= 0);

                    int i = 0;
                    int acc = 0;
                    while (i < n)
                    {
                        Spec.LoopInvariant(i >= 0 && i <= n && acc >= 0);
                        Spec.Decreases(n - i);
                        acc = acc + i;
                        i = i + 1;
                    }

                    int j = 0;
                    while (j < n)
                    {
                        Spec.LoopInvariant(j >= 0 && j <= n && acc >= 0);
                        Spec.Decreases(n - j);
                        acc = acc + j;
                        j = j + 1;
                    }
                    return acc;
                }
            }
            """;

        var validDiags = await AnalyzerTestHarness.AnalyzeAsync(validLoops, new VerifiableSmtAnalyzer());
        Assert.DoesNotContain(validDiags, d => d.Id == "AD0001");
        Assert.DoesNotContain(validDiags, d => d.Id.StartsWith("VER1"));

        const string invalidLoop = """
            using Verifiable.Contracts;

            namespace SmtSamples;

            [Verifiable]
            public static class BadLoops
            {
                public static int NonDecreasing(int n)
                {
                    Spec.Requires(n >= 0);
                    int i = 0;
                    while (i < n)
                    {
                        Spec.LoopInvariant(i >= 0 && i <= n);
                        Spec.Decreases(n - i);
                        i = i + 0;
                    }
                    return i;
                }
            }
            """;

        var badDiags = await AnalyzerTestHarness.AnalyzeAsync(invalidLoop, new VerifiableSmtAnalyzer());
        Assert.Contains(badDiags, d => d.Id == "VER102" && d.GetMessage().Contains("Decreases"));
    }

    [Fact]
    public async Task InvalidSpecUsage_EmitsVER101WithFix()
    {
        const string source = """
            using Verifiable.Contracts;

            namespace SmtSamples;

            [Verifiable]
            public static class SpecErr
            {
                public static int WrongReturnType(int x)
                {
                    Spec.Ensures<long>(result => result >= 0);
                    return x;
                }
            }
            """;

        var diags = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableSmtAnalyzer());
        var err = Assert.Single(diags.Where(d => d.Id == "VER101"));
        Assert.Contains("Fix:", err.GetMessage());
    }

    [Fact]
    public async Task AgentMode_EmitsValidSingleLineVerifiableCounterexampleJsonBlock()
    {
        const string source = """
            using Verifiable.Contracts;

            namespace SmtSamples;

            [Verifiable]
            public static class AgentFailSample
            {
                public static int Inc(int a)
                {
                    Spec.Requires(a >= 1);
                    Spec.Ensures<int>(result => result > 10);
                    return a + 1;
                }
            }
            """;

        var props = new Dictionary<string, string> { ["build_property.VerifiableAgent"] = "1" };
        var diags = await AnalyzerTestHarness.AnalyzeWithOptionsAsync(source, props, new VerifiableSmtAnalyzer());

        var err = Assert.Single(diags.Where(d => d.Id == "VER102"));
        var msg = err.GetMessage();

        var match = Regex.Match(msg, @"<verifiable-counterexample>(?<json>\{.*\})</verifiable-counterexample>");
        Assert.True(match.Success, $"Expected <verifiable-counterexample> JSON block in: {msg}");

        using var doc = JsonDocument.Parse(match.Groups["json"].Value);
        var root = doc.RootElement;
        Assert.Equal("VER102", root.GetProperty("rule").GetString());
        Assert.Equal("AgentFailSample.Inc", root.GetProperty("symbol").GetString());
        Assert.Equal("Postcondition", root.GetProperty("kind").GetString());
        Assert.True(root.GetProperty("counterexample").TryGetProperty("a", out var ceVal));
        Assert.True(int.TryParse(ceVal.GetString(), out var aVal) && aVal >= 1 && aVal + 1 <= 10);
        Assert.False(string.IsNullOrEmpty(root.GetProperty("smtHash").GetString()));
        Assert.Contains("Fix:", root.GetProperty("fix").GetString());
    }

    [Fact]
    public void SmtQueryCache_AcceleratesRepeatedQueries()
    {
        var uniqueSuffix = Guid.NewGuid().ToString("N");
        var script = $"(declare-const k_{uniqueSuffix} Int)\n(assert (> k_{uniqueSuffix} 10))\n(assert (not (> k_{uniqueSuffix} 5)))";

        var r1 = Z3SolverRunner.Execute(script);
        Assert.Equal(SmtStatus.Unsat, r1.Status);

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 50; i++)
        {
            var r2 = Z3SolverRunner.Execute(script);
            Assert.Equal(SmtStatus.Unsat, r2.Status);
        }
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 500, $"50 cached SMT queries took {sw.ElapsedMilliseconds}ms");
    }

    [Property(Iterations = 15)]
    public Property Z3Counterexamples_AreSoundForLinearIntegerContracts() =>
        (from minBound in Gen.Int32().Between(-50, 50)
         from span in Gen.Int32().Between(0, 50)
         from offset in Gen.Int32().Between(-20, 20)
         from target in Gen.Int32().Between(-60, 100)
         select (minBound, maxBound: minBound + span, offset, target)).ForAll(p =>
        {
            var source = $$"""
                using Verifiable.Contracts;
                namespace SoundnessProbe;
                [Verifiable]
                public static class Probe
                {
                    public static int Compute(int x)
                    {
                        Spec.Requires(x >= {{p.minBound}} && x <= {{p.maxBound}});
                        Spec.Ensures<int>(result => result >= {{p.target}});
                        return x + ({{p.offset}});
                    }
                }
                """;

            var props = new Dictionary<string, string> { ["build_property.VerifiableAgent"] = "1" };
            var diags = AnalyzerTestHarness.AnalyzeWithOptionsAsync(source, props, new VerifiableSmtAnalyzer())
                .GetAwaiter().GetResult();

            if (diags.Any(d => d.Id is "AD0001" or "VER101" or "VER103" or "VER104"))
            {
                return false;
            }

            var shouldHold = (p.minBound + p.offset) >= p.target;
            var failures = diags.Where(d => d.Id == "VER102").ToList();

            if (shouldHold)
            {
                return failures.Count == 0;
            }

            if (failures.Count != 1)
            {
                return false;
            }

            var match = Regex.Match(failures[0].GetMessage(), @"<verifiable-counterexample>(?<json>\{.*\})</verifiable-counterexample>");
            if (!match.Success) return false;

            using var doc = JsonDocument.Parse(match.Groups["json"].Value);
            var xText = doc.RootElement.GetProperty("counterexample").GetProperty("x").GetString();
            if (!int.TryParse(xText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ceX))
            {
                return false;
            }

            return ceX >= p.minBound && ceX <= p.maxBound && (ceX + p.offset) < p.target;
        });

    [Fact]
    public async Task TypeAliasesAndClassLevelVerifiable_PushConstraintsIntoTypesWithoutDotValueBoilerplate()
    {
        const string validSource = """
            using Verifiable.Contracts;

            namespace AliasProofs;

            [TypeAlias<int>]
            public readonly partial record struct PositiveCount { public static bool IsValid(int v) => v >= 1; }

            [TypeAlias<int>]
            public readonly partial record struct NonNegCount { public static bool IsValid(int v) => v >= 0; }

            [TypeAlias<decimal>]
            public readonly partial record struct PositiveMoney { public static bool IsValid(decimal v) => v > 0m; }

            [Verifiable]
            public readonly record struct Account(PositiveCount Version, NonNegCount TxCount, decimal Total)
            {
                [Invariant]
                public bool IsSolvent => Total >= 0m;
            }

            [Verifiable]
            public static class AliasEngine
            {
                public static Account Credit(Account acc, PositiveMoney deposit)
                {
                    Spec.Ensures<Account>(result => result.Version == acc.Version + 1 && result.Total > acc.Total);
                    var nextVer = PositiveCount.From(acc.Version + 1);
                    var nextTx = NonNegCount.From(acc.TxCount + 1);
                    return acc with { Version = nextVer, TxCount = nextTx, Total = acc.Total + deposit };
                }

                public static NonNegCount DecrementIfPositive(NonNegCount count)
                {
                    if (count <= 0)
                    {
                        return count;
                    }
                    return NonNegCount.From(count - 1);
                }
            }
            """;

        var validDiags = await AnalyzerTestHarness.AnalyzeAsync(validSource, new VerifiableSmtAnalyzer());
        Assert.DoesNotContain(validDiags, d => d.Id == "AD0001");
        Assert.DoesNotContain(validDiags, d => d.Id.StartsWith("VER1"));

        const string invalidSource = """
            using Verifiable.Contracts;

            namespace BrokenAliasProofs;

            [TypeAlias<int>]
            public readonly partial record struct NonNegCount { public static bool IsValid(int v) => v >= 0; }

            [Verifiable]
            public static class BrokenAliasEngine
            {
                public static NonNegCount UnguardedDecrement(NonNegCount count) =>
                    NonNegCount.From(count - 1);
            }
            """;

        var invalidDiags = await AnalyzerTestHarness.AnalyzeAsync(invalidSource, new VerifiableSmtAnalyzer());
        Assert.DoesNotContain(invalidDiags, d => d.Id == "AD0001");
        var ver102s = invalidDiags.Where(d => d.Id == "VER102").ToList();
        Assert.Contains(ver102s, d => d.GetMessage().Contains("Precondition") && d.GetMessage().Contains("count ="));
        Assert.Contains(ver102s, d => d.GetMessage().Contains("TypeInvariant") && d.GetMessage().Contains("count ="));
    }

    [Fact]
    public async Task ElmErrorAndSpiritStrings_SurfaceInVer102HumanMessageAndAgentJson()
    {
        const string source = """
            using Verifiable.Contracts;

            namespace SpiritSamples;

            [TypeAlias<decimal>("Deposits must be strictly positive so transfers never drain an account backwards.")]
            public readonly partial record struct PositiveDeposit
            {
                public static bool IsValid(decimal v) => v > 0m;
            }

            [Verifiable]
            public static class Vault
            {
                public static decimal Withdraw(decimal balance, decimal amount)
                {
                    Spec.Requires(amount > 0m, "Withdrawal amounts must be positive.");
                    Spec.Ensures<decimal>(result => result >= 0m, "Vault accounts do not allow overdrafts.");
                    return balance - amount;
                }

                public static PositiveDeposit MakeDeposit(decimal raw) =>
                    PositiveDeposit.From(raw);
            }
            """;

        var props = new Dictionary<string, string> { ["build_property.VerifiableAgent"] = "1" };
        var diags = await AnalyzerTestHarness.AnalyzeWithOptionsAsync(source, props, new VerifiableSmtAnalyzer());
        var ver102s = diags.Where(d => d.Id == "VER102").ToList();

        var withdrawErr = Assert.Single(ver102s, d => d.GetMessage().Contains("Vault.Withdraw"));
        var wMsg = withdrawErr.GetMessage();
        var firstLine = wMsg.Split('\n')[0];
        Assert.Contains("Postcondition of Vault.Withdraw fails", firstLine);
        Assert.Contains("Vault accounts do not allow overdrafts.", firstLine);
        Assert.Contains("balance = ", firstLine);
        Assert.Contains("-- PROOF FAILED", wMsg);
        Assert.Contains("Why this rule exists:", wMsg);
        Assert.Contains("Vault accounts do not allow overdrafts.", wMsg);
        Assert.Contains("^", wMsg);
        Assert.Contains("Fix:", wMsg);

        var match = Regex.Match(wMsg, @"<verifiable-counterexample>(?<json>\{.*\})</verifiable-counterexample>");
        Assert.True(match.Success, $"Expected <verifiable-counterexample> JSON in: {wMsg}");
        using (var doc = JsonDocument.Parse(match.Groups["json"].Value))
        {
            Assert.Equal("Vault accounts do not allow overdrafts.", doc.RootElement.GetProperty("reason").GetString());
        }

        Assert.Contains(ver102s, d => d.GetMessage().Contains("Vault.MakeDeposit") && d.GetMessage().Contains("Deposits must be strictly positive"));
    }
}
