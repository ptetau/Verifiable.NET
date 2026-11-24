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

            public static class OccurrenceExample
            {
                [Verifiable]
                [Requires("x > 5 && x <= 100")]
                [Ensures("result >= 12")]
                public static int DoubleBounded(int x) => x * 2;

                [Verifiable]
                [Ensures("result >= 0")]
                public static int Caller(int requested)
                {
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

            public static class BrokenMath
            {
                [Verifiable]
                [Requires("x >= 0")]
                [Ensures("result >= 10")]
                public static int AddFive(int x) => x + 5;
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

            public static class LedgerCheck
            {
                [Verifiable]
                [Ensures("result == 0.3m")]
                public static decimal ExactDecimalSum() => 0.1m + 0.2m;

                [Verifiable]
                [Requires("fromBal >= amount && amount > 0m")]
                [Ensures("result >= 0m")]
                public static decimal Withdraw(decimal fromBal, decimal amount) => fromBal - amount;
            }
            """;

        var validDiags = await AnalyzerTestHarness.AnalyzeAsync(validSource, new VerifiableSmtAnalyzer());
        Assert.DoesNotContain(validDiags, d => d.Id.StartsWith("VER1"));

        const string invalidSource = """
            using Verifiable.Contracts;

            namespace SmtSamples;

            public static class BrokenLedger
            {
                [Verifiable]
                [Requires("amount > 0m")]
                [Ensures("result >= 0m")]
                public static decimal UnguardedWithdraw(decimal fromBal, decimal amount) => fromBal - amount;
            }
            """;

        var badDiags = await AnalyzerTestHarness.AnalyzeAsync(invalidSource, new VerifiableSmtAnalyzer());
        Assert.Contains(badDiags, d => d.Id == "VER102" && d.GetMessage().Contains("fromBal ="));
    }

    [Fact]
    public async Task StringLengthAndRefinedString_AreVerifiedByZ3()
    {
        const string source = """
            using Verifiable.Contracts;

            namespace SmtSamples;

            [Refined<string>("Length is >= 1 and <= 200")]
            public readonly partial record struct TitleText;

            public static class Strings
            {
                [Verifiable]
                [Ensures("result >= 1 && result <= 200")]
                public static int GetLength(TitleText title) => title.Value.Length;
            }
            """;

        var diags = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableSmtAnalyzer());
        Assert.DoesNotContain(diags, d => d.Id.StartsWith("VER1"));
    }

    [Fact]
    public async Task CheckedArithmetic_DetectsOverflowAndProvesBoundedAddition()
    {
        const string overflowSource = """
            using Verifiable.Contracts;

            namespace SmtSamples;

            public static class CheckedOps
            {
                [Verifiable, CheckedArithmetic]
                public static int UnboundedAdd(int a, int b) => a + b;

                [Verifiable, CheckedArithmetic]
                [Requires("a >= 0 && a <= 1000 && b >= 0 && b <= 1000")]
                [Ensures("result >= 0 && result <= 2000")]
                public static int BoundedAdd(int a, int b) => a + b;
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

            [Refined<int>("Value >= 1")]
            public readonly partial record struct ItemId;

            [Verifiable]
            public readonly record struct Item(ItemId Id, int Weight);

            [Verifiable]
            [Invariant("NextId.Value >= 1")]
            [Invariant("ActiveCount >= 0")]
            [Invariant("Items.Count == ActiveCount")]
            [Invariant("Items.Count == NextId.Value - 1")]
            public readonly record struct Board(
                ItemId NextId,
                ImmutableDictionary<ItemId, Item> Items,
                int ActiveCount);

            public static class Engine
            {
                [Verifiable]
                [Requires("weight >= 1 && !board.Items.ContainsKey(board.NextId)")]
                [Ensures("result.NextId.Value == board.NextId.Value + 1")]
                [Ensures("result.ActiveCount == board.ActiveCount + 1")]
                [Ensures("result.Items.Count == board.Items.Count + 1")]
                public static Board AddItem(Board board, int weight)
                {
                    var item = new Item(board.NextId, weight);
                    var nextId = ItemId.From(board.NextId.Value + 1);
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
    public async Task LoopInvariantAndDecreases_PassWhenInductiveAndFailWhenBroken()
    {
        const string validLoop = """
            using Verifiable.Contracts;

            namespace SmtSamples;

            public static class Loops
            {
                [Verifiable]
                [Requires("n >= 0")]
                [LoopInvariant("i >= 0 && i <= n && acc >= 0")]
                [Decreases("n - i")]
                [Ensures("result >= 0")]
                public static int SumUp(int n)
                {
                    int i = 0;
                    int acc = 0;
                    while (i < n)
                    {
                        acc = acc + i;
                        i = i + 1;
                    }
                    return acc;
                }
            }
            """;

        var validDiags = await AnalyzerTestHarness.AnalyzeAsync(validLoop, new VerifiableSmtAnalyzer());
        Assert.DoesNotContain(validDiags, d => d.Id.StartsWith("VER1"));

        const string invalidLoop = """
            using Verifiable.Contracts;

            namespace SmtSamples;

            public static class BadLoops
            {
                [Verifiable]
                [Requires("n >= 0")]
                [LoopInvariant("i >= 0 && i <= n")]
                [Decreases("n - i")]
                public static int NonDecreasing(int n)
                {
                    int i = 0;
                    while (i < n)
                    {
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
    public async Task InvalidContractSyntax_EmitsVER101WithFix()
    {
        const string source = """
            using Verifiable.Contracts;

            namespace SmtSamples;

            public static class SyntaxErr
            {
                [Verifiable]
                [Requires("x >=")]
                [Ensures("result >= 0")]
                public static int F(int x) => x;
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

            public static class AgentFailSample
            {
                [Verifiable]
                [Requires("a >= 1")]
                [Ensures("result > 10")]
                public static int Inc(int a) => a + 1;
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
                public static class Probe
                {
                    [Verifiable]
                    [Requires("x >= {{p.minBound}} && x <= {{p.maxBound}}")]
                    [Ensures("result >= {{p.target}}")]
                    public static int Compute(int x) => x + ({{p.offset}});
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

            // Counterexample MUST satisfy precondition and violate postcondition!
            return ceX >= p.minBound && ceX <= p.maxBound && (ceX + p.offset) < p.target;
        });

    [Fact]
    public async Task TypeAliasesAndClassLevelVerifiable_PushConstraintsIntoTypesWithoutDotValueBoilerplate()
    {
        const string validSource = """
            using Verifiable.Contracts;

            [assembly: TypeAlias<int>("PositiveCount", "Value >= 1", Namespace = "AliasProofs")]
            [assembly: TypeAlias<int>("NonNegCount", "Value >= 0", Namespace = "AliasProofs")]
            [assembly: TypeAlias<decimal>("PositiveMoney", "Value > 0m", Namespace = "AliasProofs")]

            namespace AliasProofs;

            [Verifiable]
            [Invariant("Total >= 0m")]
            public readonly record struct Account(PositiveCount Version, NonNegCount TxCount, decimal Total);

            [Verifiable]
            public static class AliasEngine
            {
                [Ensures("result.Version == acc.Version + 1 && result.Total > acc.Total")]
                public static Account Credit(Account acc, PositiveMoney deposit)
                {
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

            [assembly: TypeAlias<int>("NonNegCount", "Value >= 0", Namespace = "BrokenAliasProofs")]

            namespace BrokenAliasProofs;

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
}
