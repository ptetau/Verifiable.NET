using System.Linq;
using System.Threading.Tasks;
using Verifiable.Analyzers.Smt;
using Xunit;

namespace Verifiable.Analyzers.Tests;

/// <summary>
/// Elm-style error-message catalog tests verifying formatting quality, multi-line gutter markers,
/// dual-location context snippets, context-specific hints, typo suggestions, and SMT clause pinpointing.
/// </summary>
public sealed class ElmErrorCatalogTests
{
    [Fact]
    public void ElmSuggest_RanksTranspositionsAndCaseInsensitiveTypos_AndFiltersUnrelatedNames()
    {
        var candidates = new[] { "Balance", "TotalDebits", "TotalCredits", "AccountId", "LedgerEngine.PostTransfer" };

        var bal = ElmSuggest.Rank("Balnce", candidates);
        Assert.Equal("Balance", bal[0]);

        var transposed = ElmSuggest.Rank("LedgerEngine.PostTransfre", candidates);
        Assert.Equal("LedgerEngine.PostTransfer", transposed[0]);

        var none = ElmSuggest.Rank("CompletelyDifferentIdentifier", candidates);
        Assert.Empty(none);
    }

    [Fact]
    public async Task Gatekeeper_VER002_SuggestsExactImmutableReplacement_AndDeduplicatesInitializerCascade()
    {
        const string source = """
            using System.Collections.Generic;
            using Verifiable.Contracts;

            namespace CatalogSamples;

            [Verifiable]
            public static class Collections
            {
                public static int CountItems()
                {
                    List<string> items = new List<string>();
                    return items.Count;
                }

                public static int Lookup(Dictionary<string, int> map) => map.Count;

                public static int[] RawArray() => new int[3];
            }
            """;

        var diags = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableGatekeeperAnalyzer());
        var ver002s = diags.Where(d => d.Id == "VER002").ToList();

        // CountItems should emit ONE VER002 for `List<string> items = new List<string>();` (no duplicate cascade on the initializer)
        var listDiags = ver002s.Where(d => d.GetMessage().Contains("List<string>")).ToList();
        var listDiag = Assert.Single(listDiags);
        Assert.Contains("ImmutableList<string>", listDiag.GetMessage());

        var dictDiag = Assert.Single(ver002s, d => d.GetMessage().Contains("Dictionary<string, int>"));
        Assert.Contains("ImmutableDictionary<string, int>", dictDiag.GetMessage());

        Assert.Contains(ver002s, d => d.GetMessage().Contains("ImmutableArray<int>"));
    }

    [Fact]
    public async Task Gatekeeper_VER004_TailorsHintsForClockGuidAndRandom()
    {
        const string source = """
            using System;
            using Verifiable.Contracts;

            namespace CatalogSamples;

            [Verifiable]
            public static class Ambient
            {
                public static DateTime Clock() => DateTime.UtcNow;
                public static Guid Id() => Guid.NewGuid();
                public static int Roll() => new Random().Next();
            }
            """;

        var diags = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableGatekeeperAnalyzer());
        var ver004s = diags.Where(d => d.Id == "VER004").ToList();

        var clockMsg = Assert.Single(ver004s, d => d.GetMessage().Contains("DateTime.UtcNow")).GetMessage();
        Assert.Contains("Clock read", clockMsg);
        Assert.Contains("DateTimeOffset now", clockMsg);

        var guidMsg = Assert.Single(ver004s, d => d.GetMessage().Contains("Guid.NewGuid")).GetMessage();
        Assert.Contains("ID allocation", guidMsg);
        Assert.Contains("Guid nextId", guidMsg);

        var randMsg = Assert.Single(ver004s, d => d.GetMessage().Contains("Random")).GetMessage();
        Assert.Contains("Random source", randMsg);
    }

    [Fact]
    public async Task Gatekeeper_VER005_MultiLineWhileLoop_RendersGutterMarkersAndLoopInvariantHint()
    {
        const string source = """
            using Verifiable.Contracts;

            namespace CatalogSamples;

            [Verifiable]
            public static class Loops
            {
                public static int Countdown(int n)
                {
                    while (n > 0)
                    {
                        n--;
                    }
                    return n;
                }
            }
            """;

        var diags = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableGatekeeperAnalyzer());
        var loopDiag = Assert.Single(diags, d => d.Id == "VER005");
        var msg = loopDiag.GetMessage();

        Assert.Contains("-- DISALLOWED CONTROL FLOW", msg);
        Assert.Contains("|> ", msg);
        Assert.Contains("while (n > 0)", msg);
        Assert.Contains("n--;", msg);
        Assert.Contains("Spec.LoopInvariant", msg);
        Assert.Contains("Spec.Decreases", msg);
    }

    [Fact]
    public async Task Gatekeeper_VER006_InSourceUnverifiedCallee_ShowsDualLocationSnippet()
    {
        const string source = """
            using Verifiable.Contracts;

            namespace CatalogSamples;

            public static class LegacyHelper
            {
                public static int Compute(int x) => x + 1;
            }

            [Verifiable]
            public static class PureCore
            {
                public static int Run(int x) => LegacyHelper.Compute(x);
            }
            """;

        var diags = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableGatekeeperAnalyzer());
        var callDiag = Assert.Single(diags, d => d.Id == "VER006");
        var msg = callDiag.GetMessage();

        Assert.Contains("-- UNVERIFIED CALL", msg);
        Assert.Contains("LegacyHelper.Compute", msg);
        Assert.Contains("is declared here (not `[Verifiable]`)", msg);
        Assert.Single(callDiag.AdditionalLocations);
    }

    [Fact]
    public async Task LinearObligation_DoubleConsume_ShowsDualLocationsAndSpecificTitle()
    {
        const string source = """
            using Verifiable.Contracts;

            namespace LinearCatalog;

            [MustConsume]
            public readonly record struct Receipt(int Id);

            public static class Flow
            {
                public static Receipt Create(int id) => new(id);

                [ConsumesObligation]
                public static void Send(Receipt r) { }

                public static void DoubleSend(int id)
                {
                    var receipt = Create(id);
                    Send(receipt);
                    Send(receipt);
                }
            }
            """;

        var diags = await AnalyzerTestHarness.AnalyzeAsync(source, new LinearObligationAnalyzer());
        var err = Assert.Single(diags, d => d.Id == "VER007");
        var msg = err.GetMessage();

        Assert.Contains("-- OBLIGATION ALREADY CONSUMED", msg);
        Assert.Contains("was consumed more than once", msg);
        Assert.Contains("It was already consumed here", msg);
        Assert.Single(err.AdditionalLocations);
        Assert.Contains("Fix:", msg);
    }

    [Fact]
    public async Task SmtVer102_CompoundPostcondition_PinpointsFailingClauseAndShowsDualContractLocation()
    {
        const string source = """
            using Verifiable.Contracts;

            namespace SmtCatalog;

            [Verifiable]
            public readonly record struct CounterState(int Version, int Active, int Total);

            [Verifiable]
            public static class CounterEngine
            {
                public static CounterState Increment(CounterState s)
                {
                    Spec.Requires(s.Version >= 1 && s.Active >= 0 && s.Total >= 0);
                    Spec.Ensures<CounterState>(result =>
                        result.Version == s.Version + 1
                        && result.Active == s.Active + 1
                        && result.Total == s.Total + 1,
                        "Increment must advance version, active count, and total count together.");

                    // Bug: forgot to increment Active!
                    return s with { Version = s.Version + 1, Total = s.Total + 1 };
                }
            }
            """;

        var diags = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableSmtAnalyzer());
        var err = Assert.Single(diags, d => d.Id == "VER102");
        var msg = err.GetMessage();

        Assert.Contains("-- PROOF FAILED", msg);
        Assert.Contains("Failed clause (2 of 3):", msg);
        Assert.Contains("result.Active == s.Active + 1", msg);
        Assert.Contains("Left  (result.Active):", msg);
        Assert.Contains("Right (s.Active + 1):", msg);
        Assert.Contains("The postcondition that failed is declared here", msg);
        Assert.Single(err.AdditionalLocations);
    }

    [Fact]
    public async Task SmtVer102_StrictBoundaryHit_EmitsBoundaryHint()
    {
        const string source = """
            using Verifiable.Contracts;

            namespace SmtCatalog;

            [Verifiable]
            public static class BoundaryCheck
            {
                public static int Identity(int x)
                {
                    Spec.Requires(x >= 0);
                    Spec.Ensures<int>(result => result > 0);
                    return x;
                }
            }
            """;

        var diags = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableSmtAnalyzer());
        var err = Assert.Single(diags, d => d.Id == "VER102");
        var msg = err.GetMessage();

        Assert.Contains("-- PROOF FAILED", msg);
        Assert.Contains("result = 0", msg);
        Assert.Contains("Boundary hit:", msg);
        Assert.Contains(">=", msg);
    }

    [Fact]
    public async Task NullabilitySuppressor_SuppressesZ3ProvedCS8602AndCS8604_AndPreservesUnprovedWarnings()
    {
        const string source = """
            using Verifiable.Contracts;

            namespace SuppressorCatalog;

            [Verifiable]
            public static class NullProofs
            {
                private static int Consume(string value) => value.Length;

                public static int ProvedDeref(string? name)
                {
                    Spec.Requires(!string.IsNullOrEmpty(name), "Name must be non-empty.");
                    return name.Length;
                }

                public static int ProvedArg(string? name)
                {
                    Spec.Requires(name != null, "Name must not be null.");
                    return Consume(name);
                }

                public static int UnprovedDeref(string? maybeNull)
                {
                    return maybeNull.Length;
                }
            }
            """;

        var diags = await AnalyzerTestHarness.AnalyzeIncludingSuppressedAsync(source, new VerifiableNullabilitySuppressor());

        var cs8602s = diags.Where(d => d.Id == "CS8602").OrderBy(d => d.Location.GetLineSpan().StartLinePosition.Line).ToList();
        Assert.Equal(2, cs8602s.Count);
        Assert.True(cs8602s[0].IsSuppressed, "Expected Z3-proved CS8602 in ProvedDeref to be suppressed.");
        Assert.False(cs8602s[1].IsSuppressed, "Expected unproved CS8602 in UnprovedDeref to remain active.");

        var cs8604 = Assert.Single(diags, d => d.Id == "CS8604");
        Assert.True(cs8604.IsSuppressed, "Expected Z3-proved CS8604 in ProvedArg to be suppressed.");
    }
}
