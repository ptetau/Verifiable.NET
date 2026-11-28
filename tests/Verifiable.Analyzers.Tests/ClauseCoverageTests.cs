using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Verifiable.Analyzers;
using Verifiable.Analyzers.Smt;
using Xunit;

namespace Verifiable.Analyzers.Tests;

public sealed class ClauseCoverageTests
{
    private const string Manifest = """
        { "clauses": [
            { "id": "C1", "text": "Prices are never negative." },
            { "id": "C2", "text": "A booking ends after it starts." }
        ] }
        """;

    private const string Covered = """
        using Verifiable.Contracts;

        namespace Sample;

        [TypeAlias<decimal>("Prices are never negative", Clause = "C1")]
        public readonly partial record struct Price
        {
            public static bool IsValid(decimal value) => value >= 0m;
        }

        [Verifiable]
        public static class Rules
        {
            public static int Span(int start, int end)
            {
                Spec.Requires(start >= 0 && end <= 1000 && start < end, "A booking ends after it starts.", clause: "C2");
                Spec.Ensures<int>(r => r > 0, "The span is positive.", "C2");
                return end - start;
            }
        }
        """;

    private static Task<List<Diagnostic>> Run(string source, string? manifest = Manifest) =>
        RunAll(source, manifest, new ClauseCoverageAnalyzer());

    private static async Task<List<Diagnostic>> RunAll(string source, string? manifest, params DiagnosticAnalyzer[] analyzers)
    {
        var files = manifest is null
            ? new List<(string, string)>()
            : new List<(string, string)> { ("/proj/Verifiable.Clauses.JSON", manifest) };
        var diags = await AnalyzerTestHarness.AnalyzeWithAdditionalFilesAsync(source, files, analyzers);
        return diags.Where(d => d.Id.StartsWith("VER4") || d.Id.StartsWith("CS") && d.Severity == DiagnosticSeverity.Error).ToList();
    }

    [Fact]
    public async Task EveryClauseCovered_NamedAndPositional_NoDiagnostics()
    {
        var diags = await Run(Covered);
        Assert.Empty(diags);
    }

    [Fact]
    public async Task ClauseDoesNotChangeVerdicts()
    {
        var withClause = await AnalyzerTestHarness.AnalyzeAsync(Covered, new VerifiableSmtAnalyzer());
        var without = await AnalyzerTestHarness.AnalyzeAsync(
            Covered.Replace(", clause: \"C2\"", "").Replace(", \"C2\"", "").Replace(", Clause = \"C1\"", ""),
            new VerifiableSmtAnalyzer());
        Assert.Equal(
            without.Select(d => d.Id).OrderBy(x => x),
            withClause.Select(d => d.Id).OrderBy(x => x));
        Assert.DoesNotContain(withClause, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task ClauseOnFailingContract_StillRefuted()
    {
        const string source = """
            using Verifiable.Contracts;

            [Verifiable]
            public static class Bad
            {
                public static int Inc(int x)
                {
                    Spec.Requires(x >= 0 && x < 100, "small", clause: "C1");
                    Spec.Ensures<int>(r => r > x + 1, "wrong", clause: "C2");
                    return x + 1;
                }
            }
            """;
        var diags = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableSmtAnalyzer());
        Assert.Contains(diags, d => d.Id == "VER102");
    }

    [Fact]
    public async Task NamedClauseBeforeBecause_StillProved()
    {
        const string source = """
            using Verifiable.Contracts;

            [Verifiable]
            public static class Reordered
            {
                public static int Inc(int x)
                {
                    Spec.Requires(clause: "C1", condition: x >= 0 && x < 100, because: "small");
                    Spec.EnsuresWhen<int>(x > 5, r => r > 6, clause: "C2", because: "grows");
                    Spec.Ensures<int>(r => r == x + 1, clause: "C2");
                    return x + 1;
                }
            }
            """;
        var diags = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableSmtAnalyzer());
        Assert.DoesNotContain(diags, d => d.Id.StartsWith("VER1") && d.Severity >= DiagnosticSeverity.Warning);
    }

    [Fact]
    public async Task Ver201_UnknownClause_Error()
    {
        var diags = await Run(Covered.Replace("clause: \"C2\"", "clause: \"C9\""));
        var d = Assert.Single(diags, x => x.Id == "VER401");
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        Assert.Contains("C9", d.GetMessage());
    }

    [Fact]
    public async Task Ver202_ClauseWithoutConstruct_ErrorAtManifest()
    {
        var manifest = Manifest.Replace("] }", ", { \"id\": \"C3\" } ] }");
        var diags = await Run(Covered, manifest);
        var d = Assert.Single(diags, x => x.Id == "VER402");
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        Assert.Contains("C3", d.GetMessage());
        Assert.Equal("/proj/Verifiable.Clauses.JSON", d.Location.GetLineSpan().Path);
        Assert.Contains("\"id\": \"C3\"", manifest.Substring(d.Location.SourceSpan.Start, d.Location.SourceSpan.Length));
    }

    [Fact]
    public async Task Ver203_ConstructWithoutClause_WarnsOncePerConstruct()
    {
        var source = Covered
            .Replace(", clause: \"C2\"", "")
            .Replace(", \"C2\"", "")
            .Replace(", Clause = \"C1\"", "");
        var diags = await Run(source);
        var ver203 = diags.Where(d => d.Id == "VER403").ToList();
        Assert.Equal(3, ver203.Count);
        Assert.All(ver203, d => Assert.Equal(DiagnosticSeverity.Warning, d.Severity));
        Assert.Equal(2, diags.Count(d => d.Id == "VER402"));
    }

    [Fact]
    public async Task Ver203_OutsideVerifiableScope_Silent()
    {
        const string source = """
            using Verifiable.Contracts;

            public static class NotVerified
            {
                public static int Inc(int x)
                {
                    Spec.Requires(x >= 0, "non-negative");
                    return x + 1;
                }
            }
            """;
        var diags = await Run(source, """{ "clauses": [] }""");
        Assert.Empty(diags);
    }

    [Fact]
    public async Task NoManifest_NoTraceabilityDiagnostics()
    {
        var diags = await Run(Covered.Replace("clause: \"C2\"", "clause: \"C9\"").Replace(", Clause = \"C1\"", ""), manifest: null);
        Assert.Empty(diags);
    }

    [Fact]
    public async Task Ver204_NonConstantClause_Error()
    {
        const string source = """
            using Verifiable.Contracts;

            [Verifiable]
            public static class Rules
            {
                public static int Inc(int x, string id)
                {
                    Spec.Requires(x >= 0, "non-negative", clause: id);
                    return x;
                }
            }
            """;
        var diags = await Run(source, manifest: null);
        var d = Assert.Single(diags, x => x.Id == "VER404");
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"  \"")]
    [InlineData("\"C 1\"")]
    public async Task Ver204_EmptyOrWhitespaceClause_Error(string literal)
    {
        var source = $$"""
            using Verifiable.Contracts;

            [Verifiable]
            public static class Rules
            {
                public static int Inc(int x)
                {
                    Spec.Requires(x >= 0, "non-negative", clause: {{literal}});
                    return x;
                }
            }
            """;
        var diags = await Run(source, manifest: null);
        Assert.Single(diags, x => x.Id == "VER404");
    }

    [Fact]
    public async Task ConstFieldClause_IsAccepted()
    {
        const string source = """
            using Verifiable.Contracts;

            [Verifiable]
            public static class Rules
            {
                private const string C1 = "C1";

                public static int Inc(int x)
                {
                    Spec.Requires(x >= 0, "non-negative", clause: C1);
                    return x;
                }
            }
            """;
        var diags = await Run(source, """{ "clauses": [ { "id": "C1" } ] }""");
        Assert.Empty(diags);
    }

    [Theory]
    [InlineData("""{ "clauses": [ { "id": "C1" }, { "id": "C1" } ] }""", "more than once")]
    [InlineData("""{ "clauses": [ { "id": "" } ] }""", "not valid")]
    [InlineData("""{ "clauses": [ { "id": "C 1" } ] }""", "not valid")]
    [InlineData("""{ "clauses": [ { "text": "x" } ] }""", "\"id\"")]
    [InlineData("""{ "clauses": [ """, "not valid JSON")]
    [InlineData("""[ ]""", "\"clauses\" array")]
    public async Task MalformedManifest_Ver204AtFile_AndNothingElse(string manifest, string expected)
    {
        var diags = await Run(Covered, manifest);
        Assert.All(diags, d => Assert.Equal("VER404", d.Id));
        var d = Assert.Single(diags);
        Assert.Contains(expected, d.GetMessage());
        Assert.Equal("/proj/Verifiable.Clauses.JSON", d.Location.GetLineSpan().Path);
    }

    [Fact]
    public async Task InvariantAndRefinedAttributes_CarryClause()
    {
        const string source = """
            using Verifiable.Contracts;

            namespace Sample;

            [Refined<int>("Positive", Clause = "C1")]
            public readonly partial record struct Count
            {
                public static bool IsValid(int value) => value > 0;
            }

            [Verifiable]
            public sealed class Box
            {
                public int Size { get; init; }

                [Invariant("Size is non-negative", Clause = "C2")]
                public bool SizeOk => Size >= 0;
            }
            """;
        Assert.Empty(await Run(source));

        var diags = await Run(source.Replace(", Clause = \"C2\"", ""));
        Assert.Single(diags, x => x.Id == "VER403");
        Assert.Single(diags, x => x.Id == "VER402");
    }

    [Fact]
    public async Task ClauseOnEveryContractForm_IsAccepted()
    {
        const string source = """
            using Verifiable.Contracts;

            [Verifiable]
            public static class Forms
            {
                public static int F(int n, bool g)
                {
                    Spec.Requires(n >= 0 && n < 1000, "range", clause: "C1");
                    Spec.RequiresWhen(g, n > 0, "when g", clause: "C1");
                    Spec.Ensures<int>(r => r >= 0, "non-negative", clause: "C1");
                    Spec.EnsuresWhen<int>(g, r => r > 0, "when g", clause: "C1");
                    var i = 0;
                    var acc = 0;
                    while (i < n)
                    {
                        Spec.LoopInvariant(i >= 0 && i <= n && acc == i, "tracks i", clause: "C1");
                        Spec.Decreases(n - i, "n - i shrinks", clause: "C1");
                        acc++;
                        i++;
                    }
                    return acc;
                }
            }
            """;
        var all = await RunAll(source, """{ "clauses": [ { "id": "C1" } ] }""", new ClauseCoverageAnalyzer());
        Assert.Empty(all);
        var diags = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableSmtAnalyzer());
        Assert.DoesNotContain(diags, x => x.Id.StartsWith("VER1") && x.Severity >= DiagnosticSeverity.Warning);
    }
}
