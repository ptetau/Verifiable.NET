using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Verifiable.Analyzers;
using Xunit;

namespace Verifiable.Query.Tests;

/// <summary>
/// Requirement R13: given a hole, clause or symbol, one command returns everything an agent needs to
/// work on it, and nothing else, within a token budget.
/// </summary>
[Collection("CurrentDirectory")]
public sealed class ContextPackTests
{
    private static string Repo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Verifiable.NET.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    public static IEnumerable<object[]> SampleProjects()
    {
        yield return [Path.Combine("samples", "AccountingApp", "AccountingApp.Shared", "AccountingApp.Shared.csproj")];
        yield return [Path.Combine("samples", "TodoApp", "TodoApp.Shared", "TodoApp.Shared.csproj")];
        yield return [Path.Combine("samples", "InventoryPricing", "InventoryPricing.csproj")];
    }

    private static bool IsHandWritten(ISymbol s) =>
        !s.IsImplicitlyDeclared &&
        s.Locations.Any(l => l.IsInSource && !(l.SourceTree?.FilePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ?? true));

    /// <summary>
    /// The proxy for "an agent can fill any hole from its pack with no other reads": for every verifiable
    /// method in the samples, every hand-written type and method its real body uses is in its pack.
    /// </summary>
    [Theory]
    [MemberData(nameof(SampleProjects))]
    public void EverySampleBody_UsesOnlyWhatItsPackContains(string relativeProject)
    {
        var loaded = WorkspaceLoader.Load(Path.Combine(Repo(), relativeProject), Array.Empty<string>(), null);
        Assert.Null(loaded.ErrorMessage);
        var compilation = loaded.Compilation!;
        var gaps = new List<string>();
        var checkedMethods = 0;

        foreach (var tree in compilation.SyntaxTrees.Where(t => !t.FilePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)))
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var decl in tree.GetRoot().DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(decl) is not IMethodSymbol method) continue;
                if (VerifiableSymbolHelpers.GetScope(method) != VerificationScope.Verifiable) continue;
                SyntaxNode? body = (SyntaxNode?)decl.Body ?? decl.ExpressionBody;
                if (body is null) continue;

                var pack = ContextPacks.Build(compilation, method, new Dictionary<string, string?>());
                var packTypes = new HashSet<string>(pack.Types.Select(t => t.Name), StringComparer.Ordinal);
                var packHelpers = new HashSet<string>(pack.Helpers.Select(h => h.Symbol), StringComparer.Ordinal);
                checkedMethods++;

                foreach (var node in body.DescendantNodesAndSelf())
                {
                    if (node is ExpressionSyntax expr)
                    {
                        foreach (var t in new[] { model.GetTypeInfo(expr).Type, model.GetTypeInfo(expr).ConvertedType })
                        {
                            foreach (var named in Flatten(t))
                            {
                                if (IsHandWritten(named) && !packTypes.Contains(named.Name))
                                    gaps.Add($"{pack.Symbol}: type {named.Name} (used at line {expr.GetLocation().GetLineSpan().StartLinePosition.Line + 1})");
                            }
                        }
                    }

                    if (node is InvocationExpressionSyntax or ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax &&
                        model.GetSymbolInfo(node).Symbol is IMethodSymbol called &&
                        IsHandWritten(called.OriginalDefinition) &&
                        called.MethodKind is MethodKind.Ordinary or MethodKind.Constructor &&
                        !SymbolEqualityComparer.Default.Equals(called.OriginalDefinition, method) &&
                        called.ContainingType?.ToDisplayString() != VerifiableSymbolHelpers.SpecTypeName)
                    {
                        var name = ContextPacks.Name(called.OriginalDefinition);
                        var isRule = called.Name == "IsValid" || called.GetAttributes().Any(a => a.AttributeClass?.Name == "InvariantAttribute");
                        // A constructor is described by its type's declaration (record parameters included).
                        var isCtor = called.MethodKind == MethodKind.Constructor;
                        if (!packHelpers.Contains(name) && !((isRule || isCtor) && packTypes.Contains(called.ContainingType!.Name)))
                            gaps.Add($"{pack.Symbol}: helper {name}");
                    }
                }
            }
        }

        Assert.True(checkedMethods > 5, $"Only {checkedMethods} methods checked in {relativeProject}.");
        Assert.True(gaps.Count == 0, $"{gaps.Count} gaps in {relativeProject}:\n" + string.Join("\n", gaps.Distinct().OrderBy(g => g, StringComparer.Ordinal)));
    }

    private static IEnumerable<INamedTypeSymbol> Flatten(ITypeSymbol? t)
    {
        switch (t)
        {
            case IArrayTypeSymbol a:
                foreach (var x in Flatten(a.ElementType)) yield return x;
                break;
            case INamedTypeSymbol n:
                yield return n.OriginalDefinition;
                foreach (var arg in n.TypeArguments)
                    foreach (var x in Flatten(arg)) yield return x;
                break;
        }
    }

    private const string Source = """
        using Verifiable.Contracts;

        [TypeAlias<int>("Nights run from 1 to 30.", Clause = "C1")]
        public readonly partial record struct Nights
        {
            public static bool IsValid(int value) => value >= 1 && value <= 30;
        }

        public sealed record Booking(int Start, int End)
        {
            [Invariant("A booking ends after it starts", Clause = "C2")]
            public bool EndsAfterStart => End > Start;

            public int Length() => End - Start;
        }

        [Verifiable]
        public static class Schedule
        {
            public static bool Overlaps(Booking a, Booking b)
            {
                Spec.Requires(a.End > a.Start && b.End > b.Start, "Bookings end after they start.", clause: "C2");
                return a.Start < b.End && b.Start < a.End;
            }

            public static bool CanBook(Booking a, Booking b, Nights nights)
            {
                Spec.Requires(a.End > a.Start && b.End > b.Start, "Bookings end after they start.", clause: "C2");
                Spec.Ensures<bool>(r => r == !Overlaps(a, b), "Double bookings are refused.", clause: "C3");
                return Spec.Hole<bool>("C3");
            }

            public static int Price(Nights nights)
            {
                Spec.Requires(nights.Value >= 1 && nights.Value <= 30, "Stays are short.", clause: "C1");
                return nights.Value * 100;
            }

            public static int Quote(Booking a, Booking b, Nights nights)
            {
                Spec.Requires(a.End > a.Start && b.End > b.Start, "Bookings end after they start.", clause: "C2");
                return CanBook(a, b, nights) ? Price(nights) : 0;
            }
        }

        [Verifiable]
        public static class Unrelated
        {
            public static int Twice(int x)
            {
                Spec.Requires(x >= 0 && x <= 100, "Small.");
                return x * 2;
            }
        }
        """;

    private static (int Code, JsonElement Root) Json(params string[] args)
    {
        var dir = Path.Combine(Path.GetTempPath(), "vq-pack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var src = Path.Combine(dir, "Schedule.cs");
        File.WriteAllText(src, Source);
        File.WriteAllText(Path.Combine(dir, "spec.clauses.json"),
            "{\"clauses\":[{\"id\":\"C1\",\"text\":\"Stays are 1 to 30 nights.\"},{\"id\":\"C2\",\"text\":\"A booking ends after it starts.\"},{\"id\":\"C3\",\"text\":\"Double bookings are refused.\"}]}");
        using var o = new StringWriter();
        using var e = new StringWriter();
        var full = args.Select(a => a.Replace("{src}", src).Replace("{manifest}", Path.Combine(dir, "spec.clauses.json"))).Append("--format").Append("json").ToArray();
        var code = Program.Run(full, null, o, e);
        return (code, JsonDocument.Parse(o.ToString()).RootElement.Clone());
    }

    [Fact]
    public void HolePack_HoldsSignatureContractsClauseTextTypesHelpersAndCallers()
    {
        var (code, root) = Json("pack", "--file", "{src}", "--holes", "--clauses", "{manifest}");
        Assert.Equal(0, code);
        Assert.Equal("verifiable-query/pack@1", root.GetProperty("schema").GetString());
        var pack = Assert.Single(root.GetProperty("packs").EnumerateArray());

        Assert.Equal("Schedule.CanBook", pack.GetProperty("symbol").GetString());
        Assert.True(pack.GetProperty("hole").GetBoolean());
        Assert.Equal("public static bool CanBook(Booking a, Booking b, Nights nights)", pack.GetProperty("signature").GetString());
        Assert.Equal(2, pack.GetProperty("contracts").GetArrayLength());
        var clauses = pack.GetProperty("clauses").EnumerateArray().ToDictionary(c => c.GetProperty("id").GetString()!, c => c.GetProperty("text").GetString());
        Assert.Equal("Double bookings are refused.", clauses["C3"]);

        var types = pack.GetProperty("types").EnumerateArray().ToDictionary(t => t.GetProperty("name").GetString()!);
        Assert.Contains("Booking", types.Keys);
        Assert.Contains("Nights", types.Keys);
        Assert.Contains("EndsAfterStart", types["Booking"].GetProperty("source").GetString());
        Assert.DoesNotContain("Length()", types["Booking"].GetProperty("source").GetString());
        Assert.Equal("C2", types["Booking"].GetProperty("rules")[0].GetProperty("clause").GetString());
        Assert.Contains("value >= 1 && value <= 30", types["Nights"].GetProperty("source").GetString());

        var helpers = pack.GetProperty("helpers").EnumerateArray().ToList();
        Assert.Equal("Schedule.Overlaps", helpers[0].GetProperty("symbol").GetString());
        Assert.Equal(0, helpers[0].GetProperty("rank").GetInt32());
        Assert.Contains(helpers, h => h.GetProperty("symbol").GetString() == "Booking.Length");
        Assert.Contains(helpers, h => h.GetProperty("symbol").GetString() == "Unrelated.Twice" && h.GetProperty("rank").GetInt32() == 3);

        var caller = Assert.Single(pack.GetProperty("callers").EnumerateArray());
        Assert.Equal("Schedule.Quote", caller.GetProperty("symbol").GetString());
        Assert.Contains("CanBook(a, b, nights)", caller.GetProperty("callSite").GetString());
        Assert.Single(caller.GetProperty("contracts").EnumerateArray());
    }

    [Fact]
    public void MaxTokens_TrimsTheLeastRelevantFirstAndListsWhatItLeftOut()
    {
        var tokenDir = Path.Combine(Path.GetTempPath(), "vq-pack-tokens-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tokenDir);
        var srcFile = Path.Combine(tokenDir, "Schedule.cs");
        File.WriteAllText(srcFile, Source);
        var compilation = WorkspaceLoader.Load(null, new[] { srcFile }, null).Compilation!;
        var target = ContextPacks.Resolve(compilation, new[] { "Schedule.CanBook" }).Single();
        var fullTokens = ContextPacks.EstimateTokens(ContextPacks.RenderOne(ContextPacks.Build(compilation, target, new Dictionary<string, string?>())));

        var (_, root) = Json("pack", "--file", "{src}", "--target", "Schedule.CanBook", "--max-tokens", (fullTokens * 9 / 10).ToString(System.Globalization.CultureInfo.InvariantCulture));
        var trimmed = root.GetProperty("packs")[0];
        var omitted = trimmed.GetProperty("omitted").EnumerateArray().Select(o => o.GetProperty("section").GetString() + ":" + o.GetProperty("name").GetString()).ToList();
        Assert.Contains("helpers:Unrelated.Twice", omitted);
        Assert.Contains(trimmed.GetProperty("helpers").EnumerateArray(), h => h.GetProperty("symbol").GetString() == "Schedule.Overlaps");

        var (_, tiny) = Json("pack", "--file", "{src}", "--target", "Schedule.CanBook", "--max-tokens", "1");
        var bare = tiny.GetProperty("packs")[0];
        Assert.Equal(2, bare.GetProperty("contracts").GetArrayLength());
        Assert.Equal(0, bare.GetProperty("helpers").GetArrayLength());
        Assert.Equal(0, bare.GetProperty("callers").GetArrayLength());
        Assert.Contains(bare.GetProperty("types").EnumerateArray(), t => t.GetProperty("name").GetString() == "Schedule");
        Assert.True(bare.GetProperty("omitted").GetArrayLength() >= 4);
    }

    [Fact]
    public void Pack_ByClause_And_BadInputs()
    {
        var (code, root) = Json("pack", "--file", "{src}", "--clause", "C1");
        Assert.Equal(0, code);
        Assert.Equal(new[] { "Schedule.Price" }, root.GetProperty("packs").EnumerateArray().Select(p => p.GetProperty("symbol").GetString()).ToArray());

        Assert.Equal(3, Json("pack", "--file", "{src}").Code);
        Assert.Equal(3, Json("pack", "--file", "{src}", "--target", "Nope.Nothing").Code);
        Assert.Equal(3, Json("pack", "--file", "{src}", "--target", "Schedule.CanBook", "--max-tokens", "zero").Code);
    }

    [Fact]
    public void Pack_IsByteIdenticalAcrossRuns()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vq-pack-same");
        Directory.CreateDirectory(dir);
        var src = Path.Combine(dir, "Schedule.cs");
        File.WriteAllText(src, Source);
        string Run()
        {
            using var o = new StringWriter();
            Program.Run(["pack", "--file", src, "--holes", "--format", "json"], null, o, new StringWriter());
            return o.ToString();
        }
        Assert.Equal(Run(), Run());
    }
}
