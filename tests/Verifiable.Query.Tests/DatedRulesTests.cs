using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Verifiable.Query.Tests;

/// <summary>
/// Requirement R6: a table of values with "from" dates generates a lookup function; gaps and overlaps
/// fail the build naming the dates; a rule declares the date it takes effect and each version is proved
/// on its own.
/// </summary>
[Collection("CurrentDirectory")]
public sealed class DatedRulesTests
{
    private const string Fees = """
        using System;
        using Verifiable.Contracts;

        [Verifiable]
        public static partial class Fees
        {
            [EffectiveFrom("2026-01-01", Rule = "Cleaning", Clause = "C5")]
            public static int CleaningV1(int nights)
            {
                Spec.Requires(nights >= 1 && nights <= 30, "Stays are short.");
                Spec.Ensures<int>(r => r == 50, "A flat fee.");
                return 50;
            }

            [EffectiveFrom("2026-07-01", Rule = "Cleaning", Clause = "C5")]
            public static int CleaningV2(int nights)
            {
                Spec.Requires(nights >= 1 && nights <= 30, "Stays are short.");
                Spec.Ensures<int>(r => r == 10 * nights, "Ten a night.");
                return 10 * nights;
            }

            public static decimal Rate(DateOnly day)
            {
                Spec.Requires(day.DayNumber >= NightlyRate.FirstDayNumber, "Dates the table covers.");
                Spec.Ensures<decimal>(r => r >= 100m, "Rates are at least 100.");
                return NightlyRate.For(day);
            }
        }
        """;

    private const string Table = "# type: decimal\nfrom,to,value\n2026-01-01,2026-06-30,100\n2026-07-01,,120\n";

    private static string Setup(string fees = Fees, string table = Table)
    {
        var dir = Path.Combine(Path.GetTempPath(), "vq-dated-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Fees.cs"), fees);
        File.WriteAllText(Path.Combine(dir, "NightlyRate.values.csv"), table);
        return dir;
    }

    private static (int Code, JsonElement Root) Check(string dir)
    {
        using var o = new StringWriter();
        var code = Program.Run(["check", "--project", dir, "--format", "json"], null, o, new StringWriter());
        return (code, JsonDocument.Parse(o.ToString()).RootElement.Clone());
    }

    private static string[] Rules(JsonElement root) =>
        root.GetProperty("diagnostics").EnumerateArray().Select(d => d.GetProperty("rule").GetString()!).ToArray();

    [Fact]
    public void TableAndVersions_AreGeneratedAndProved()
    {
        var (code, root) = Check(Setup());
        Assert.True(code == 0, root.GetRawText());
    }

    [Fact]
    public void GeneratedCode_ReturnsTheValueAndVersionInForceOnEachDate()
    {
        var dir = Setup();
        var loaded = WorkspaceLoader.Load(dir, Array.Empty<string>(), null);
        var (assembly, error) = DataCheck.Load(loaded.Compilation!);
        Assert.Null(error);

        var rate = assembly!.GetType("NightlyRate")!.GetMethod("For")!;
        Assert.Equal(100m, rate.Invoke(null, [new DateOnly(2026, 6, 30)]));
        Assert.Equal(120m, rate.Invoke(null, [new DateOnly(2026, 7, 1)]));
        Assert.Equal(120m, rate.Invoke(null, [new DateOnly(2030, 1, 1)]));

        var cleaning = assembly.GetType("Fees")!.GetMethod("Cleaning")!;
        Assert.Equal(50, cleaning.Invoke(null, [new DateOnly(2026, 3, 1), 3]));
        Assert.Equal(30, cleaning.Invoke(null, [new DateOnly(2026, 7, 1), 3]));
    }

    [Fact]
    public void EachVersion_IsProvedOnItsOwn()
    {
        var (code, root) = Check(Setup(Fees.Replace("return 10 * nights;", "return 11 * nights;")));
        Assert.Equal(1, code);
        var failure = Assert.Single(root.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("VER102", failure.GetProperty("rule").GetString());
        Assert.Equal("Fees.CleaningV2", failure.GetProperty("symbol").GetString());
    }

    [Fact]
    public void Callers_ProveAgainstTheTableValues()
    {
        var (code, root) = Check(Setup(Fees.Replace("r >= 100m", "r >= 110m")));
        Assert.Equal(1, code);
        var failure = root.GetProperty("diagnostics").EnumerateArray().Single(d => d.GetProperty("rule").GetString() == "VER102");
        Assert.Equal("Fees.Rate", failure.GetProperty("symbol").GetString());
    }

    [Theory]
    [InlineData("2026-07-01,,120", "2026-07-05,,120", "VER204", "No value applies from 2026-07-01 to 2026-07-04")]
    [InlineData("2026-01-01,2026-06-30,100", "2026-01-01,2026-07-03,100", "VER205", "2026-01-01..2026-07-03 (line 3) overlaps the range starting 2026-07-01")]
    [InlineData("2026-07-01,,120", "2026-01-01,,120", "VER205", "Two rows start on 2026-01-01")]
    [InlineData("# type: decimal", "# type: money", "VER206", "not supported")]
    [InlineData("2026-07-01,,120", "2026-13-01,,120", "VER206", "not a yyyy-MM-dd date")]
    public void BadTables_FailNamingTheDates(string from, string to, string rule, string message)
    {
        Assert.Contains(from, Table);
        var (code, root) = Check(Setup(table: Table.Replace(from, to)));
        Assert.NotEqual(0, code);
        var hit = root.GetProperty("diagnostics").EnumerateArray().First(d => d.GetProperty("rule").GetString() == rule);
        Assert.Contains(message, hit.GetProperty("summary").GetString());
    }

    [Theory]
    [InlineData("[EffectiveFrom(\"2026-07-01\", Rule = \"Cleaning\", Clause = \"C5\")]", "[EffectiveFrom(\"2026-01-01\", Rule = \"Cleaning\", Clause = \"C5\")]", "take effect on 2026-01-01")]
    [InlineData("public static int CleaningV2(int nights)", "public static int CleaningV2(long nights)", "must share one signature")]
    [InlineData("public static partial class Fees", "public static class Fees", "static partial class")]
    public void BadVersions_AreReported(string from, string to, string message)
    {
        var dir = Setup(Fees.Replace(from, to).Replace("return NightlyRate.For(day);", "return NightlyRate.For(day);"));
        var (_, root) = Check(dir);
        var hit = root.GetProperty("diagnostics").EnumerateArray().FirstOrDefault(d => d.GetProperty("rule").GetString() == "VER207");
        Assert.True(hit.ValueKind == JsonValueKind.Object, root.GetRawText());
        Assert.Contains(message, hit.GetProperty("summary").GetString());
    }
}
