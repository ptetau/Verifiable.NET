using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Verifiable.Query.Tests;

/// <summary>
/// Requirement R2: every contract, type rule and invariant is exported in a stable, documented JSON
/// format with its symbol, expression tree, reason and clause; the same source gives byte-identical output.
/// </summary>
[Collection("CurrentDirectory")]
public sealed class ContractExportTests
{
    private const string Source = """
        using System.Collections.Immutable;
        using Verifiable.Contracts;

        [TypeAlias<string>("Names are 1 to 100 characters.", Clause = "C1")]
        public readonly partial record struct Name
        {
            public static bool IsValid(string value) => value is { Length: >= 1 and <= 100 };
        }

        public sealed record Stay(int Nights, bool Vip)
        {
            [Invariant("A stay is at least one night.", Clause = "C2")]
            public bool AtLeastOneNight => Nights >= 1;
        }

        [Verifiable]
        public static class Rates
        {
            public static int Price(Stay s)
            {
                Spec.Requires(s.Nights >= 1 && s.Nights <= 30, "Stays are short.", clause: "C2");
                Spec.RequiresWhen(s.Vip, s.Nights <= 10, "VIP stays are capped.", clause: "C3");
                Spec.Ensures<int>(r => r > 0 && Spec.Implies(s.Vip, r <= int.MaxValue), "Prices are positive.", clause: "C4");
                return s.Vip ? s.Nights * 90 : s.Nights * 100;
            }

            public static int Sum(ImmutableArray<int> xs)
            {
                Spec.Requires(!xs.IsDefault && xs.Length <= 100 && Spec.ForAll(0, xs.Length, i => xs[i] >= 0 && xs[i] <= 1000), "Small non-negative values.");
                Spec.Ensures<int>(r => r >= 0, "Sums of non-negatives are non-negative.");
                var total = 0;
                for (var i = 0; i < xs.Length; i++)
                {
                    Spec.LoopInvariant(total >= 0 && total <= i * 1000, "The running total stays in range.");
                    Spec.Decreases(xs.Length - i, "One item per step.");
                    total += xs[i];
                }
                return total;
            }
        }
        """;

    private static string Write()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vq-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "Rates.cs");
        File.WriteAllText(path, Source);
        return path;
    }

    private static string Run(params string[] args)
    {
        using var o = new StringWriter();
        Program.Run(args, null, o, new StringWriter());
        return o.ToString();
    }

    private static List<JsonElement> Contracts(string file) =>
        JsonDocument.Parse(Run("export", "--file", file, "--format", "json")).RootElement.GetProperty("contracts").EnumerateArray().Select(e => e.Clone()).ToList();

    [Fact]
    public void Export_CoversEveryKindWithSymbolReasonAndClause()
    {
        var all = Contracts(Write());
        var kinds = all.Select(c => (c.GetProperty("kind").GetString(), c.GetProperty("symbol").GetString())).ToList();

        Assert.Contains(("typeAlias", "Name"), kinds);
        Assert.Contains(("invariant", "Stay.AtLeastOneNight"), kinds);
        Assert.Equal(2, kinds.Count(k => k == ("requires", "Rates.Price(Stay)")));
        Assert.Contains(("ensures", "Rates.Price(Stay)"), kinds);
        Assert.Contains(("loopInvariant", "Rates.Sum(ImmutableArray<int>)"), kinds);
        Assert.Contains(("decreases", "Rates.Sum(ImmutableArray<int>)"), kinds);

        var vip = all.Single(c => c.GetProperty("clause").GetString() == "C3");
        Assert.Equal("VIP stays are capped.", vip.GetProperty("reason").GetString());
        Assert.Equal("member", vip.GetProperty("guard").GetProperty("op").GetString());
        Assert.Equal("le", vip.GetProperty("expr").GetProperty("op").GetString());

        var ens = all.Single(c => c.GetProperty("clause").GetString() == "C4");
        Assert.Equal("r", ens.GetProperty("result").GetString());
        Assert.Equal("result", ens.GetProperty("expr").GetProperty("left").GetProperty("left").GetProperty("op").GetString());
        var implies = ens.GetProperty("expr").GetProperty("right");
        Assert.Equal("implies", implies.GetProperty("op").GetString());
        Assert.Equal("static", implies.GetProperty("right").GetProperty("right").GetProperty("op").GetString());
    }

    [Fact]
    public void ExpressionTrees_HaveNoFallbacksForTheSupportedForms()
    {
        var all = Contracts(Write());
        var json = string.Join("\n", all.Select(c => c.GetRawText()));
        Assert.DoesNotContain("\"op\":\"csharp\"", json);

        var name = all.Single(c => c.GetProperty("symbol").GetString() == "Name").GetProperty("expr");
        Assert.Equal("and", name.GetProperty("op").GetString());
        Assert.Equal("notNull", name.GetProperty("left").GetProperty("op").GetString());
        var length = name.GetProperty("right").GetProperty("left");
        Assert.Equal("ge", length.GetProperty("op").GetString());
        Assert.Equal("Length", length.GetProperty("left").GetProperty("name").GetString());

        var forall = all.First(c => c.GetProperty("kind").GetString() == "requires" && c.GetProperty("symbol").GetString()!.StartsWith("Rates.Sum"))
            .GetProperty("expr").GetProperty("right");
        Assert.Equal("forall", forall.GetProperty("op").GetString());
        Assert.Equal("i", forall.GetProperty("variable").GetString());
        Assert.Equal("index", forall.GetProperty("body").GetProperty("left").GetProperty("left").GetProperty("op").GetString());
    }

    [Fact]
    public void Export_IsByteIdenticalAndTheFileHoldsEverything()
    {
        var file = Write();
        var dir = Path.GetDirectoryName(file)!;
        Assert.Equal(Run("export", "--file", file, "--format", "json"), Run("export", "--file", file, "--format", "json"));

        var a = Path.Combine(dir, "a.json");
        var b = Path.Combine(dir, "b.json");
        Run("export", "--file", file, "--out", a, "--format", "json");
        Run("export", "--file", file, "--out", b, "--limit", "1", "--format", "json");
        Assert.Equal(File.ReadAllText(a), File.ReadAllText(b));
        var doc = JsonDocument.Parse(File.ReadAllText(a)).RootElement;
        Assert.Equal(doc.GetProperty("page").GetProperty("total").GetInt32(), doc.GetProperty("contracts").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, doc.GetProperty("page").GetProperty("nextCursor").ValueKind);
    }

    [Theory]
    [InlineData("samples/AccountingApp/AccountingApp.Shared/AccountingApp.Shared.csproj")]
    [InlineData("samples/TodoApp/TodoApp.Shared/TodoApp.Shared.csproj")]
    [InlineData("samples/InventoryPricing/InventoryPricing.csproj")]
    public void SampleContracts_ExportAsFullTrees(string project)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Verifiable.NET.sln"))) dir = dir.Parent;
        var output = Path.Combine(Path.GetTempPath(), "vq-export-sample-" + Guid.NewGuid().ToString("N") + ".json");
        Run("export", "--project", Path.Combine(dir!.FullName, project), "--out", output);
        var doc = JsonDocument.Parse(File.ReadAllText(output)).RootElement;
        var contracts = doc.GetProperty("contracts").EnumerateArray().ToList();

        Assert.True(contracts.Count > 10);
        Assert.All(contracts, c => Assert.NotEqual(JsonValueKind.Null, c.GetProperty("expr").ValueKind));
        Assert.DoesNotContain("\"op\":\"csharp\"", doc.GetRawText().Replace(" ", string.Empty));
    }
}
