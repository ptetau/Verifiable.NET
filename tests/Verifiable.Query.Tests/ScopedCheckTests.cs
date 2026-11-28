using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Verifiable.Query.Tests;

/// <summary>
/// Requirement R14: an agent can check one method, type or clause in seconds, unchanged proofs come
/// from the cache, and a timeout names the exact obligation that timed out.
/// </summary>
[Collection("CurrentDirectory")]
public sealed class ScopedCheckTests
{
    private const string TwoBrokenMethods = """
        using Verifiable.Contracts;

        [Verifiable]
        public static class Shop
        {
            public static int Discount(int price)
            {
                Spec.Requires(price >= 0 && price <= 1000, "Prices are small.");
                Spec.Ensures<int>(r => r < price, "A discount always lowers the price.");
                return price - price / 10;
            }

            public static int Tax(int price)
            {
                Spec.Requires(price >= 0 && price <= 1000, "Prices are small.");
                Spec.Ensures<int>(r => r > price, "Tax always raises the price.");
                return price + price / 10;
            }
        }

        [Verifiable]
        public static class Other
        {
            public static int Same(int x)
            {
                Spec.Requires(x >= 0 && x <= 10, "Small.");
                Spec.Ensures<int>(r => r == x + 1, "Off by one on purpose.");
                return x;
            }
        }
        """;

    private const string HardSource = """
        using Verifiable.Contracts;

        [Verifiable]
        public static class Hard
        {
            public static long Product(long a, long b)
            {
                Spec.Requires(a > 1 && a < 100000 && b > 1 && b < 100000, "Inputs are bounded.");
                Spec.Ensures<long>(r => r != 1000003L * 999983L, "The product is never this semiprime.");
                return a * a * b;
            }
        }
        """;

    private static string Repo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Verifiable.NET.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static string Temp(string source)
    {
        var dir = Path.Combine(Path.GetTempPath(), "vq-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "Src.cs");
        File.WriteAllText(path, source);
        return path;
    }

    private static (int Code, JsonElement Root) Json(params string[] args)
    {
        using var o = new StringWriter();
        using var e = new StringWriter();
        var code = Program.Run(args.Append("--format").Append("json").ToArray(), null, o, e);
        return (code, JsonDocument.Parse(o.ToString()).RootElement.Clone());
    }

    private static string[] Symbols(JsonElement root) =>
        root.GetProperty("diagnostics").EnumerateArray().Select(d => d.GetProperty("symbol").GetString()!).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToArray();

    [Fact]
    public void TargetedCheck_ReportsOnlyTheSymbolsAskedFor()
    {
        var file = Temp(TwoBrokenMethods);

        var all = Json("check", "--file", file);
        Assert.Equal(1, all.Code);
        Assert.Equal(new[] { "Other.Same", "Shop.Discount", "Shop.Tax" }, Symbols(all.Root));

        var one = Json("check", "--file", file, "--target", "Shop.Tax");
        Assert.Equal(1, one.Code);
        Assert.Equal(new[] { "Shop.Tax" }, Symbols(one.Root));

        var type = Json("check", "--file", file, "--symbol", "Shop");
        Assert.Equal(new[] { "Shop.Discount", "Shop.Tax" }, Symbols(type.Root));

        var two = Json("check", "--file", file, "--target", "Shop.Discount,Other.Same");
        Assert.Equal(new[] { "Other.Same", "Shop.Discount" }, Symbols(two.Root));
    }

    [Fact]
    public void TargetedCheck_OfAProvedMethodPasses()
    {
        var file = Temp(TwoBrokenMethods.Replace("r > price", "r >= price"));
        var (code, root) = Json("check", "--file", file, "--target", "Shop.Tax");
        Assert.Equal(0, code);
        Assert.Equal("proved", root.GetProperty("status").GetString());
    }

    [Fact]
    public void TargetedCheck_UnknownSymbol_IsAnInputErrorWithSuggestions()
    {
        var file = Temp(TwoBrokenMethods);
        var (code, root) = Json("check", "--file", file, "--target", "Shop.Tx");
        Assert.Equal(3, code);
        Assert.Equal("UNKNOWN SYMBOL", root.GetProperty("error").GetProperty("title").GetString());
        Assert.Contains("Shop.Tax", root.GetProperty("error").GetProperty("fix").GetString());
    }

    [Fact]
    public void Timeout_IsUndecidedAndNamesTheExactObligation()
    {
        var file = Temp(HardSource);
        var (code, root) = Json("check", "--file", file, "--timeout", "1");

        Assert.Equal(2, code);
        Assert.Equal("undecided", root.GetProperty("status").GetString());
        var diags = root.GetProperty("diagnostics").EnumerateArray().ToList();
        Assert.NotEmpty(diags);
        foreach (var d in diags)
        {
            Assert.Equal("VER103", d.GetProperty("rule").GetString());
            Assert.Equal("Hard.Product", d.GetProperty("symbol").GetString());
            Assert.False(string.IsNullOrEmpty(d.GetProperty("obligation").GetString()));
            Assert.False(string.IsNullOrEmpty(d.GetProperty("contract").GetString()));
            Assert.True(d.GetProperty("line").GetInt32() > 0);
            Assert.Contains("helper", d.GetProperty("fix").GetString());
        }
    }

    [Fact]
    public void UnchangedProofs_ComeFromTheCache()
    {
        var file = Temp(TwoBrokenMethods.Replace("r > price", "r >= price").Replace("r < price", "r <= price").Replace("r == x + 1", "r == x"));
        var first = Json("check", "--file", file, "--target", "Shop", "--stats");
        var second = Json("check", "--file", file, "--target", "Shop", "--stats");

        Assert.Equal(0, first.Code);
        Assert.Equal(0, second.Code);
        var stats = second.Root.GetProperty("stats");
        Assert.True(stats.GetProperty("queries").GetInt64() > 0);
        Assert.Equal(0, stats.GetProperty("solverRuns").GetInt64());
        Assert.Equal(
            stats.GetProperty("queries").GetInt64(),
            stats.GetProperty("memoryCacheHits").GetInt64() + stats.GetProperty("diskCacheHits").GetInt64());
    }

    [Fact]
    public void Stats_AreOffByDefaultSoOutputStaysByteIdentical()
    {
        var file = Temp(TwoBrokenMethods);
        var (_, root) = Json("check", "--file", file, "--target", "Shop.Tax");
        Assert.Equal(JsonValueKind.Null, root.GetProperty("stats").ValueKind);
    }

    /// <summary>
    /// Speed guard for the fill loop. The agreed target is open question 7; until then this holds a
    /// warm single-method check of a real sample to well under its measured cost (about 0.7 s in-process).
    /// </summary>
    [Fact]
    public void WarmSingleMethodCheck_OfASample_StaysFast()
    {
        var csproj = Path.Combine(Repo(), "samples", "AccountingApp", "AccountingApp.Shared", "AccountingApp.Shared.csproj");
        Json("check", "--project", csproj, "--target", "LedgerEngine.PostTransfer");
        var (code, root) = Json("check", "--project", csproj, "--target", "LedgerEngine.PostTransfer", "--stats");

        Assert.Equal(0, code);
        var stats = root.GetProperty("stats");
        Assert.Equal(0, stats.GetProperty("solverRuns").GetInt64());
        Assert.True(stats.GetProperty("elapsedMs").GetInt64() < 5000, $"Warm scoped check took {stats.GetProperty("elapsedMs").GetInt64()} ms.");
    }

    [Theory]
    [InlineData("Shop.Tax", "Shop.Tax", true)]
    [InlineData("Shop.Tax", "Shop", true)]
    [InlineData("Shop.Tax", "My.Namespace.Shop", true)]
    [InlineData("Shop.Tax", "My.Namespace.Shop.Tax", true)]
    [InlineData("Shop.Tax", "Shop.Discount", false)]
    [InlineData("Shop.Tax", "Other", false)]
    [InlineData(null, "Shop", false)]
    public void ScopeMatching(string? symbol, string entry, bool expected) =>
        Assert.Equal(expected, ElmCheckEngine.SymbolInScope(symbol, new[] { entry }));
}
