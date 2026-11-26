using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using GalaxyCheck;
using GalaxyCheck.Gens;
using Verifiable.Contracts;
using Xunit;

namespace Verifiable.Analyzers.Tests;

public class GatekeeperTests
{
    [Fact]
    public async Task RecognizesPureSpecialTypeHelpersWithoutTrustingAllPrimitiveMethods()
    {
        const string source = """
            using Verifiable.Contracts;
            [Verifiable] public static class Rules
            {
                public static bool Blank(string? s) => string.IsNullOrEmpty(s);
                public static decimal Magnitude(decimal n) => decimal.Abs(n);
                public static int CultureSensitive(string s) => int.Parse(s);
            }
            """;
        var diagnostics = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableGatekeeperAnalyzer());
        var rejected = Assert.Single(diagnostics.Where(d => d.Id is "VER006" or "AD0001"));
        Assert.Contains("Parse", rejected.GetMessage());
    }

    [Fact]
    public async Task CleanVerifiableCage_WithTypeAliasAndImmutableTypes_ProducesZeroDiagnostics()
    {
        const string source = """
            using System.Collections.Immutable;
            using Verifiable.Contracts;

            [assembly: Verifiable]

            namespace CleanDomain;

            [TypeAlias<int>]
            public readonly partial record struct ItemId
            {
                public static bool IsValid(int value) => value >= 1;
            }

            public readonly record struct Item(ItemId Id, string Title);

            public readonly record struct Board(ItemId NextId, ImmutableDictionary<ItemId, Item> Items);

            public static class Engine
            {
                public static Board Add(Board board, string title)
                {
                    var item = new Item(board.NextId, title);
                    var next = ItemId.From(board.NextId + 1);
                    return board with { NextId = next, Items = board.Items.Add(board.NextId, item) };
                }

                public static int BoundedSum(int n)
                {
                    var acc = 0;
                    for (var i = 0; i < n; i++)
                    {
                        acc += i;
                    }
                    return acc;
                }
            }
            """;

        var diags = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableGatekeeperAnalyzer());
        Assert.DoesNotContain(diags, d => d.Id == "AD0001");
        Assert.DoesNotContain(diags, d => d.Id.StartsWith("VER"));
        Assert.DoesNotContain(diags, d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("VER001", "mutable class", "[{ATTR}] public class BadClass { public int X { get; set; } }")]
    [InlineData("VER001", "non-readonly struct", "[{ATTR}] public struct BadStruct { public int X; }")]
    [InlineData("VER001", "ref parameter", "public static class C { [{ATTR}] public static void F(ref int x) { x = 1; } }")]
    [InlineData("VER002", "List<int>", "using System.Collections.Generic; public static class C { [{ATTR}] public static int F(List<int> xs) => xs.Count; }")]
    [InlineData("VER002", "array creation", "public static class C { [{ATTR}] public static int[] F() => new int[3]; }")]
    [InlineData("VER003", "default(TypeAlias)", "[TypeAlias<int>] public readonly partial record struct Id { public static bool IsValid(int v) => v >= 1; } public static class C { [{ATTR}] public static Id F() => default(Id); }")]
    [InlineData("VER003", "new Invariant()", "public readonly record struct Pos(int X) { [Invariant] public bool NonNeg() => X >= 0; } public static class C { [{ATTR}] public static Pos F() => new Pos(); }")]
    [InlineData("VER004", "DateTime.UtcNow", "using System; public static class C { [{ATTR}] public static DateTime F() => DateTime.UtcNow; }")]
    [InlineData("VER004", "Guid.NewGuid()", "using System; public static class C { [{ATTR}] public static Guid F() => Guid.NewGuid(); }")]
    [InlineData("VER004", "mutable static field", "[{ATTR}] public static class C { public static int Counter = 0; }")]
    [InlineData("VER005", "throw", "using System; public static class C { [{ATTR}] public static int F(int x) { if (x < 0) throw new ArgumentException(); return x; } }")]
    [InlineData("VER005", "unannotated while", "public static class C { [{ATTR}] public static int F(int n) { while (n > 0) n--; return n; } }")]
    [InlineData("VER005", "async Task", "using System.Threading.Tasks; public static class C { [{ATTR}] public static async Task<int> F() { await Task.CompletedTask; return 1; } }")]
    [InlineData("VER005", "goto", "public static class C { [{ATTR}] public static int F(int x) { goto done; done: return x; } }")]
    [InlineData("VER006", "unverified call", "public static class Ext { public static int Impure(int x) => x + 1; } public static class C { [{ATTR}] public static int F(int x) => Ext.Impure(x); }")]
    public async Task Violations_EmitExpectedErrorInVerifiable_AndWarningInCandidate_WithoutAD0001(
        string expectedId,
        string _,
        string template)
    {
        var verifiableCode = "using Verifiable.Contracts;\n" + template.Replace("{ATTR}", "Verifiable");
        var candidateCode = "using Verifiable.Contracts;\n" + template.Replace("{ATTR}", "VerifiableCandidate");

        var vDiags = await AnalyzerTestHarness.AnalyzeAsync(verifiableCode, new VerifiableGatekeeperAnalyzer());
        var cDiags = await AnalyzerTestHarness.AnalyzeAsync(candidateCode, new VerifiableGatekeeperAnalyzer());

        Assert.DoesNotContain(vDiags, d => d.Id == "AD0001");
        Assert.DoesNotContain(cDiags, d => d.Id == "AD0001");

        var vMatch = vDiags.Where(d => d.Id == expectedId).ToList();
        var cMatch = cDiags.Where(d => d.Id == expectedId).ToList();

        Assert.NotEmpty(vMatch);
        Assert.NotEmpty(cMatch);
        Assert.All(vMatch, d =>
        {
            Assert.Equal(Microsoft.CodeAnalysis.DiagnosticSeverity.Error, d.Severity);
            Assert.Contains("Fix:", d.GetMessage());
        });
        Assert.All(cMatch, d =>
        {
            Assert.Equal(Microsoft.CodeAnalysis.DiagnosticSeverity.Warning, d.Severity);
            Assert.Contains("Fix:", d.GetMessage());
        });
    }

    [Property(Iterations = 12)]
    public Property Gatekeeper_CandidateAlwaysProducesWarning_AndVerifiableProducesError() =>
        Gen.Element(new[]
        {
            ("VER001", "public static class C { [{ATTR}] public static void F(out int x) { x = 1; } }"),
            ("VER002", "using System.Collections.Generic; public static class C { [{ATTR}] public static int F() => new Dictionary<int, int>().Count; }"),
            ("VER004", "using System; public static class C { [{ATTR}] public static long F() => DateTimeOffset.UtcNow.ToUnixTimeSeconds(); }"),
            ("VER005", "using System; public static class C { [{ATTR}] public static int F() => throw new InvalidOperationException(); }"),
            ("VER006", "public static class Helper { public static int H() => 1; } public static class C { [{ATTR}] public static int F() => Helper.H(); }")
        }).ForAll(pair =>
        {
            var (id, template) = pair;
            var vDiags = AnalyzerTestHarness.AnalyzeAsync(
                "using Verifiable.Contracts;\n" + template.Replace("{ATTR}", "Verifiable"),
                new VerifiableGatekeeperAnalyzer()).GetAwaiter().GetResult();
            var cDiags = AnalyzerTestHarness.AnalyzeAsync(
                "using Verifiable.Contracts;\n" + template.Replace("{ATTR}", "VerifiableCandidate"),
                new VerifiableGatekeeperAnalyzer()).GetAwaiter().GetResult();

            return !vDiags.Any(d => d.Id == "AD0001")
                && !cDiags.Any(d => d.Id == "AD0001")
                && vDiags.Any(d => d.Id == id && d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error && d.GetMessage().Contains("Fix:"))
                && cDiags.Any(d => d.Id == id && d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Warning && d.GetMessage().Contains("Fix:"));
        });

    [Fact]
    public async Task HierarchicalScopes_CascadeFromAssemblyAndClass_AndHonorUnverifiableOptOut()
    {
        const string source = """
            using System;
            using Verifiable.Contracts;

            [assembly: Verifiable]

            namespace Scoped;

            [TypeAlias<int>]
            public readonly partial record struct PositiveId
            {
                public static bool IsValid(int v) => v >= 1;
            }

            public static class Boundary
            {
                [Unverifiable]
                public static Guid NextGuid() => Guid.NewGuid();
            }

            public static class CascadedEngine
            {
                public static PositiveId CleanMethod(PositiveId id) => PositiveId.From(id + 1);

                public static Guid BadDirectUtcNow() => DateTime.UtcNow;

                public static Guid BadCallToUnverifiable() => Boundary.NextGuid();
            }
            """;

        var diags = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableGatekeeperAnalyzer());
        Assert.DoesNotContain(diags, d => d.Id == "AD0001");

        Assert.DoesNotContain(diags, d => d.Id == "VER004" && d.Location.GetLineSpan().StartLinePosition.Line < 18);
        Assert.Contains(diags, d => d.Id == "VER004" && d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.Contains(diags, d => d.Id == "VER006" && d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task TypeLevelVerifiableCandidate_OverriddenByNestedVerifiable_HasExpectedSeverities()
    {
        const string source = """
            using System;
            using Verifiable.Contracts;

            [assembly: VerifiableCandidate]

            namespace MixedScopes;

            public static class MigrationClass
            {
                public static DateTime LegacyWarning() => DateTime.UtcNow;
            }

            [Verifiable]
            public static class PromotedClass
            {
                public static DateTime PromotedError() => DateTime.UtcNow;
            }
            """;

        var diags = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableGatekeeperAnalyzer());
        Assert.DoesNotContain(diags, d => d.Id == "AD0001");

        var warnings = diags.Where(d => d.Id == "VER004" && d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Warning).ToList();
        var errors = diags.Where(d => d.Id == "VER004" && d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToList();

        Assert.Single(warnings);
        Assert.Single(errors);
    }

    [Fact]
    public async Task ElmFormatting_IncludesBannerSnippetCaretAndFix()
    {
        const string source = """
            using System;
            using Verifiable.Contracts;

            namespace ElmFormatSample;

            [Verifiable]
            public static class ClockSample
            {
                public static DateTime ReadNow() => DateTime.UtcNow;
            }
            """;

        var diags = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableGatekeeperAnalyzer());
        var err = Assert.Single(diags.Where(d => d.Id == "VER004"));
        var msg = err.GetMessage();
        Assert.Contains("-- AMBIENT AUTHORITY", msg);
        Assert.Contains("Why this rule exists:", msg);
        Assert.Contains("DateTime.UtcNow", msg);
        Assert.Contains("^", msg);
        Assert.Contains("Fix:", msg);
    }
}
