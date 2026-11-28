using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Verifiable.Query.Tests;

/// <summary>
/// Requirement R7: compare two contract exports, label each change widened, narrowed or changed using
/// Z3, and list every proof that now fails with the changed contracts it depends on.
/// </summary>
[Collection("CurrentDirectory")]
public sealed class ChangeImpactTests
{
    private const string V1 = """
        using Verifiable.Contracts;

        [TypeAlias<int>("Stays are 1 to 30 nights.", Clause = "C1")]
        public readonly partial record struct Nights
        {
            public static bool IsValid(int value) => value >= 1 && value <= 30;
        }

        [Verifiable]
        public static class Rates
        {
            public static int Price(int nights)
            {
                Spec.Requires(nights >= 1 && nights <= 30, "Stays are short.", clause: "C1");
                Spec.Ensures<int>(r => r <= 3000, "Prices are capped.", clause: "C2");
                return nights * 100;
            }

            public static int Quote(int n)
            {
                Spec.Requires(n >= 1 && n <= 30, "Stays are short.", clause: "C1");
                Spec.Ensures<int>(r => r <= 3000, "Quotes are capped.", clause: "C3");
                return Price(n);
            }
        }
        """;

    private static string Dir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vq-impact-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static (int Code, JsonElement Root) Run(params string[] args)
    {
        using var o = new StringWriter();
        var code = Program.Run(args.Append("--format").Append("json").ToArray(), null, o, new StringWriter());
        return (code, JsonDocument.Parse(o.ToString()).RootElement.Clone());
    }

    private static (int Code, JsonElement Root) Impact(string v2, bool viaExports = false)
    {
        var dir = Dir();
        File.WriteAllText(Path.Combine(dir, "v1.cs"), V1);
        File.WriteAllText(Path.Combine(dir, "v2.cs"), v2);
        Run("export", "--file", Path.Combine(dir, "v1.cs"), "--out", Path.Combine(dir, "v1.json"));
        if (!viaExports) return Run("impact", "--old", Path.Combine(dir, "v1.json"), "--file", Path.Combine(dir, "v2.cs"));
        Run("export", "--file", Path.Combine(dir, "v2.cs"), "--out", Path.Combine(dir, "v2.json"));
        return Run("impact", "--old", Path.Combine(dir, "v1.json"), "--new", Path.Combine(dir, "v2.json"));
    }

    private static string ChangeOf(JsonElement root, string symbol, string kind) =>
        root.GetProperty("changes").EnumerateArray()
            .Single(c => c.GetProperty("symbol").GetString() == symbol && c.GetProperty("kind").GetString() == kind)
            .GetProperty("change").GetString()!;

    [Theory]
    [InlineData("nights >= 1 && nights <= 30", "nights >= 1 && nights <= 60", "widened")]
    [InlineData("nights >= 1 && nights <= 30", "nights >= 5 && nights <= 30", "narrowed")]
    [InlineData("nights >= 1 && nights <= 30", "nights >= 10 && nights <= 60", "changed")]
    [InlineData("nights >= 1 && nights <= 30", "!(nights < 1 || nights > 30)", "equivalent")]
    [InlineData("nights >= 1 && nights <= 30", "nights >= 1 && nights / 2 <= 15", "unknown")]
    public void ChangedPreconditions_AreLabelledByZ3(string from, string to, string expected)
    {
        var (_, root) = Impact(V1.Replace(from, to).Replace("n >= 1 && n <= 30", "n >= 1 && n <= 30"), viaExports: true);
        Assert.Equal("verifiable-query/impact@1", root.GetProperty("schema").GetString());
        Assert.Equal(expected, ChangeOf(root, "Rates.Price(int)", "requires"));
    }

    [Fact]
    public void AddedAndRemovedContracts_AreReported()
    {
        var v2 = V1
            .Replace("        Spec.Ensures<int>(r => r <= 3000, \"Prices are capped.\", clause: \"C2\");\n", string.Empty)
            .Replace("Spec.Ensures<int>(r => r <= 3000, \"Quotes are capped.\", clause: \"C3\");",
                     "Spec.Ensures<int>(r => r <= 3000, \"Quotes are capped.\", clause: \"C3\");\n        Spec.Ensures<int>(r => r >= 100, \"Quotes have a floor.\", clause: \"C4\");");
        Assert.NotEqual(V1, v2);
        var (_, root) = Impact(v2, viaExports: true);
        Assert.Equal("removed", ChangeOf(root, "Rates.Price(int)", "ensures"));
        var quote = root.GetProperty("changes").EnumerateArray().Single(c => c.GetProperty("symbol").GetString() == "Rates.Quote(int)");
        Assert.Equal("added", quote.GetProperty("change").GetString());
        Assert.Equal("C4", quote.GetProperty("clause").GetString());
    }

    [Fact]
    public void UnchangedCode_HasNoChangesAndNoBrokenProofs()
    {
        var (code, root) = Impact(V1);
        Assert.Equal(0, code);
        Assert.Empty(root.GetProperty("changes").EnumerateArray());
        Assert.Empty(root.GetProperty("brokenProofs").EnumerateArray());
    }

    [Fact]
    public void BrokenProofs_AreListedWithTheChangedContractsTheyDependOn()
    {
        var v2 = V1
            .Replace("nights >= 1 && nights <= 30", "nights >= 1 && nights <= 60")
            .Replace("value >= 1 && value <= 30", "value >= 1 && value <= 20")
            .Replace("r => r <= 3000, \"Quotes are capped.\"", "r => r <= 2000, \"Quotes are capped.\"");
        var (code, root) = Impact(v2);

        Assert.Equal(1, code);
        Assert.Equal("narrowed", ChangeOf(root, "Nights", "typeAlias"));
        Assert.Equal("widened", ChangeOf(root, "Rates.Price(int)", "requires"));
        Assert.Equal("narrowed", ChangeOf(root, "Rates.Quote(int)", "ensures"));

        var broken = root.GetProperty("brokenProofs").EnumerateArray().ToList();
        var price = broken.Single(b => b.GetProperty("symbol").GetString() == "Rates.Price");
        Assert.Equal("Postcondition", price.GetProperty("obligation").GetString());
        Assert.Equal(new[] { "Rates.Price(int)/requires" },
            price.GetProperty("dependsOn").EnumerateArray().Select(d => d.GetProperty("symbol").GetString() + "/" + d.GetProperty("kind").GetString()).ToArray());

        var quote = broken.Single(b => b.GetProperty("symbol").GetString() == "Rates.Quote");
        var deps = quote.GetProperty("dependsOn").EnumerateArray().Select(d => d.GetProperty("symbol").GetString() + "/" + d.GetProperty("kind").GetString()).ToList();
        Assert.Contains("Rates.Quote(int)/ensures", deps);
        Assert.Contains("Rates.Price(int)/requires", deps);
    }

    [Fact]
    public void BadInputs_AreInputErrors()
    {
        var dir = Dir();
        Assert.Equal(3, Run("impact").Code);
        Assert.Equal(3, Run("impact", "--old", Path.Combine(dir, "missing.json"), "--new", Path.Combine(dir, "missing.json")).Code);
        File.WriteAllText(Path.Combine(dir, "bad.json"), "{\"not\":\"an export\"}");
        Assert.Equal(3, Run("impact", "--old", Path.Combine(dir, "bad.json"), "--new", Path.Combine(dir, "bad.json")).Code);
    }
}
