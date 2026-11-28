using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Verifiable.Analyzers;
using Xunit;

namespace Verifiable.Query.Tests;

/// <summary>
/// Requirement R1: contracts, type aliases and invariants carry spec clause IDs; the build fails on a
/// clause with no code and warns on code with no clause; a JSON report maps clauses to symbols.
/// </summary>
[Collection("CurrentDirectory")]
public sealed class ClauseTests
{
    private const string Source = """
        using Verifiable.Contracts;

        [TypeAlias<decimal>("Prices are never negative", Clause = "C1")]
        public readonly partial record struct Price
        {
            public static bool IsValid(decimal value) => value >= 0m;
        }

        public sealed record Booking(int Start, int End)
        {
            [Invariant("A booking ends after it starts", Clause = "C2")]
            public bool EndsAfterStart => End > Start;
        }

        [Verifiable]
        public static class Bookings
        {
            public static int Length(int start, int end)
            {
                Spec.Requires(start >= 0 && end <= 10000, "Times are in range.", clause: "C2");
                Spec.Ensures<int>(r => r > 0, "A booking ends after it starts", clause: "C2");
                return end - start;
            }

            public static int Fee(int nights)
            {
                Spec.Requires(nights >= 1 && nights <= 30, "Stays are 1 to 30 nights.");
                return nights * 100;
            }

            public static int Nights(int n)
            {
                Spec.Requires(n >= 0 && n <= 30, "Small.", "C4");
                Spec.Ensures<int>(clause: "C4", because: "Never negative.", predicate: r => r >= 0);
                return n;
            }
        }
        """;

    private const string Manifest = """
        {"clauses":[
          {"id":"C1","text":"Prices are never negative."},
          {"id":"C2","text":"A booking ends after it starts."},
          {"id":"C3","text":"Double bookings are refused."},
          {"id":"C4","text":"Nights are never negative."}
        ]}
        """;

    private static (string Dir, string Src, string Manifest) Setup(string? source = null, string? manifest = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "vq-clause-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var src = Path.Combine(dir, "Booking.cs");
        var man = Path.Combine(dir, "spec.clauses.json");
        File.WriteAllText(src, source ?? Source);
        File.WriteAllText(man, manifest ?? Manifest);
        return (dir, src, man);
    }

    private static (int Code, JsonElement Root) Json(params string[] args)
    {
        using var o = new StringWriter();
        using var e = new StringWriter();
        var code = Program.Run(args.Append("--format").Append("json").ToArray(), null, o, e);
        return (code, JsonDocument.Parse(o.ToString()).RootElement.Clone());
    }

    private static JsonElement[] Diags(JsonElement root, string rule) =>
        root.GetProperty("diagnostics").EnumerateArray().Where(d => d.GetProperty("rule").GetString() == rule).ToArray();

    [Fact]
    public void Check_WithManifest_FailsOnAClauseWithNoCodeAndWarnsOnCodeWithNoClause()
    {
        var (_, src, man) = Setup();
        var (code, root) = Json("check", "--file", src, "--clauses", man);

        Assert.Equal(1, code);
        var missing = Assert.Single(Diags(root, "VER401"));
        Assert.Equal("error", missing.GetProperty("severity").GetString());
        Assert.Equal("C3", missing.GetProperty("clause").GetString());
        Assert.Contains("Double bookings are refused.", missing.GetProperty("summary").GetString());

        var unclaused = Assert.Single(Diags(root, "VER402"));
        Assert.Equal("warning", unclaused.GetProperty("severity").GetString());
        Assert.Equal("Bookings.Fee", unclaused.GetProperty("symbol").GetString());
    }

    [Fact]
    public void ProofFailures_CarryTheClauseOfTheirContract()
    {
        var (_, src, _) = Setup();
        var (_, root) = Json("check", "--file", src);

        var failures = Diags(root, "VER102").Where(d => d.GetProperty("symbol").GetString() == "Bookings.Length").ToArray();
        Assert.Contains(failures, d => d.GetProperty("obligation").GetString() == "Postcondition");
        Assert.Contains(failures, d => d.GetProperty("obligation").GetString() == "Overflow");
        Assert.All(failures, d => Assert.Equal("C2", d.GetProperty("clause").GetString()));
    }

    [Fact]
    public void Check_WithoutManifest_ReportsNoCoverageDiagnostics()
    {
        var (_, src, _) = Setup();
        var (_, root) = Json("check", "--file", src);
        Assert.DoesNotContain(root.GetProperty("diagnostics").EnumerateArray(),
            d => d.GetProperty("rule").GetString()!.StartsWith("VER40", StringComparison.Ordinal));
    }

    [Fact]
    public void Check_FailsOnAClauseIdTheSpecDoesNotHave()
    {
        var (_, src, man) = Setup(Source.Replace("\"A booking ends after it starts\", clause: \"C2\"", "\"A booking ends after it starts\", clause: \"C22\""));
        var (code, root) = Json("check", "--file", src, "--clauses", man);
        Assert.Equal(1, code);
        var unknown = Assert.Single(Diags(root, "VER403"));
        Assert.Equal("C22", unknown.GetProperty("clause").GetString());
        Assert.Equal("Bookings.Length", unknown.GetProperty("symbol").GetString());
    }

    [Fact]
    public void ClausesReport_MapsEachClauseToTheConstructsThatCarryIt()
    {
        var (dir, src, man) = Setup();
        var old = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(dir);
        try
        {
            var (code, root) = Json("clauses", "--file", src, "--clauses", man);
            Assert.Equal(1, code);
            Assert.Equal("verifiable-query/clauses@1", root.GetProperty("schema").GetString());
            Assert.Equal(new[] { "C3" }, root.GetProperty("missing").EnumerateArray().Select(e => e.GetString()).ToArray());

            var byId = root.GetProperty("clauses").EnumerateArray().ToDictionary(c => c.GetProperty("id").GetString()!);
            Assert.Equal("Prices are never negative.", byId["C1"].GetProperty("text").GetString());
            Assert.Equal(new[] { "Price" }, byId["C1"].GetProperty("constructs").EnumerateArray().Select(k => k.GetProperty("symbol").GetString()).ToArray());
            var c2 = byId["C2"].GetProperty("constructs").EnumerateArray().Select(k => (k.GetProperty("kind").GetString(), k.GetProperty("symbol").GetString())).ToArray();
            Assert.Contains(("Spec.Requires", "Bookings.Length"), c2);
            Assert.Contains(("Spec.Ensures", "Bookings.Length"), c2);
            Assert.Contains(c2, k => k.Item2 == "Booking.EndsAfterStart");
            Assert.Equal(2, byId["C4"].GetProperty("constructs").GetArrayLength());
            Assert.Equal("Booking.cs", byId["C1"].GetProperty("constructs")[0].GetProperty("file").GetString());

            var without = root.GetProperty("withoutClause").EnumerateArray().Select(k => k.GetProperty("symbol").GetString()).ToArray();
            Assert.Equal(new[] { "Bookings.Fee" }, without);
        }
        finally
        {
            Directory.SetCurrentDirectory(old);
        }
    }

    [Fact]
    public void ClausesReport_WithoutManifest_ListsWhatTheCodeCarries()
    {
        var (_, src, _) = Setup();
        var (code, root) = Json("clauses", "--file", src);
        Assert.Equal(0, code);
        Assert.Equal(new[] { "C1", "C2", "C4" }, root.GetProperty("clauses").EnumerateArray().Select(c => c.GetProperty("id").GetString()).ToArray());
    }

    [Fact]
    public void Check_ByClause_ProvesOnlyTheSymbolsThatCarryIt()
    {
        var (_, src, _) = Setup();
        var (code, root) = Json("check", "--file", src, "--clause", "C2");
        Assert.Equal(1, code);
        Assert.All(root.GetProperty("diagnostics").EnumerateArray(),
            d => Assert.StartsWith("Booking", d.GetProperty("symbol").GetString()));
        Assert.Contains(root.GetProperty("diagnostics").EnumerateArray(), d => d.GetProperty("symbol").GetString() == "Bookings.Length");

        var none = Json("check", "--file", src, "--clause", "C9");
        Assert.Equal(3, none.Code);
    }

    [Theory]
    [InlineData("{\"clauses\":[{\"id\":\"C1\"},{\"id\":\"C1\"}]}", "twice")]
    [InlineData("{\"clauses\":[{\"text\":\"no id\"}]}", "id")]
    [InlineData("{\"clauses\":{}}", "clauses")]
    [InlineData("{\"clauses\":[}", "at character")]
    [InlineData("[]", "clauses")]
    public void Manifest_RejectsMalformedInput(string json, string expected)
    {
        Assert.False(ClauseCoverageAnalyzer.TryParseManifest(json, out _, out var error));
        Assert.Contains(expected, error);
    }

    [Fact]
    public void Manifest_ReadsIdsTextAndEscapes()
    {
        Assert.True(ClauseCoverageAnalyzer.TryParseManifest(
            "{ \"version\": 1, \"clauses\": [ {\"id\":\"C1\",\"text\":\"Say \\\"hi\\\" \\u00e9\",\"extra\":[1,2.5,true,null]}, {\"id\":\"C2\"} ] }",
            out var clauses, out var error), error);
        Assert.Equal(2, clauses.Count);
        Assert.Equal("Say \"hi\" é", clauses[0].Text);
        Assert.Null(clauses[1].Text);
    }

    [Fact]
    public void Check_BadManifestFile_IsAnInputError()
    {
        var (dir, src, _) = Setup(manifest: "{not json");
        Assert.Equal(3, Json("check", "--file", src, "--clauses", Path.Combine(dir, "spec.clauses.json")).Code);
        Assert.Equal(3, Json("check", "--file", src, "--clauses", Path.Combine(dir, "missing.clauses.json")).Code);
    }
}
