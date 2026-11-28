using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Verifiable.Analyzers;
using Xunit;

namespace Verifiable.Query.Tests;

/// <summary>
/// Requirement R3: contracts the generator wrote cannot be changed by whoever fills in the body. Any
/// change to a locked method's signature or contracts fails with VER109 naming the method and clause.
/// </summary>
[Collection("CurrentDirectory")]
public sealed class ContractLockTests
{
    private const string Generated = """
        using Verifiable.Contracts;

        [Verifiable]
        public static class Schedule
        {
            public static bool Overlaps(int aStart, int aEnd, int bStart, int bEnd)
            {
                Spec.Requires(aStart < aEnd && bStart < bEnd, "Bookings end after they start.", clause: "C2");
                return aStart < bEnd && bStart < aEnd;
            }

            public static bool CanBook(int aStart, int aEnd, int bStart, int bEnd)
            {
                Spec.Requires(aStart < aEnd && bStart < bEnd, "Bookings end after they start.", clause: "C2");
                Spec.Ensures<bool>(r => r == !Overlaps(aStart, aEnd, bStart, bEnd), "Double bookings are refused.", clause: "C3");
                return Spec.Hole<bool>("C3");
            }
        }
        """;

    private const string Filled = "return !Overlaps(aStart, aEnd, bStart, bEnd);";

    private static string Dir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vq-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static (int Code, JsonElement Root) Json(params string[] args)
    {
        using var o = new StringWriter();
        using var e = new StringWriter();
        var code = Program.Run(args.Append("--format").Append("json").ToArray(), null, o, e);
        return (code, JsonDocument.Parse(o.ToString()).RootElement.Clone());
    }

    /// <summary>Locks the generated code, then checks <paramref name="edited"/> against that lock.</summary>
    private static (int Code, JsonElement[] Locks) CheckEdit(string edited)
    {
        var dir = Dir();
        var gen = Path.Combine(dir, "Generated.cs");
        File.WriteAllText(gen, Generated);
        var lockFile = Path.Combine(dir, "gen.contracts.lock.json");
        Assert.Equal(0, Json("lock", "--file", gen, "--out", lockFile).Code);

        var editedFile = Path.Combine(dir, "Edited.cs");
        File.WriteAllText(editedFile, edited);
        var (code, root) = Json("check", "--file", editedFile, "--lock", lockFile);
        var locks = root.GetProperty("diagnostics").EnumerateArray().Where(d => d.GetProperty("rule").GetString() == "VER109").ToArray();
        return (code, locks);
    }

    [Fact]
    public void Lock_IsDeterministicAndRecordsClauses()
    {
        var dir = Dir();
        var gen = Path.Combine(dir, "Generated.cs");
        File.WriteAllText(gen, Generated);
        var a = Path.Combine(dir, "a.contracts.lock.json");
        var b = Path.Combine(dir, "b.contracts.lock.json");
        var (code, root) = Json("lock", "--file", gen, "--out", a);
        Json("lock", "--file", gen, "--out", b);

        Assert.Equal(0, code);
        Assert.Equal("verifiable-query/lock@1", root.GetProperty("schema").GetString());
        Assert.Equal(File.ReadAllText(a), File.ReadAllText(b));
        Assert.True(ContractLock.TryParse(File.ReadAllText(a), out var entries, out var error), error);
        Assert.Equal(new[] { "Schedule.CanBook(int, int, int, int)", "Schedule.Overlaps(int, int, int, int)" }, entries.Select(e => e.Symbol).ToArray());
        Assert.Equal(new[] { "C2", "C3" }, entries[0].Clauses);
    }

    [Fact]
    public void FillingOnlyTheBody_PassesTheLock()
    {
        var (code, locks) = CheckEdit(Generated.Replace("return Spec.Hole<bool>(\"C3\");", Filled));
        Assert.Empty(locks);
        Assert.Equal(0, code);
    }

    [Fact]
    public void ReformattingTheContracts_PassesTheLock()
    {
        var reformatted = Generated
            .Replace("Spec.Ensures<bool>(r => r == !Overlaps(aStart, aEnd, bStart, bEnd), \"Double bookings are refused.\", clause: \"C3\");",
                     "Spec.Ensures<bool>(\n            r =>   r == !Overlaps(aStart, aEnd, bStart, bEnd),\n            \"Double bookings are refused.\",\n            clause: \"C3\");")
            .Replace("return Spec.Hole<bool>(\"C3\");", Filled);
        Assert.Empty(CheckEdit(reformatted).Locks);
    }

    [Theory]
    [InlineData("weakened ensures", "r => r == !Overlaps(aStart, aEnd, bStart, bEnd)", "r => true")]
    [InlineData("changed reason", "\"Double bookings are refused.\"", "\"Anything goes.\"")]
    [InlineData("changed clause", "clause: \"C3\");\n        return", "clause: \"C9\");\n        return")]
    [InlineData("removed ensures", "Spec.Ensures<bool>(r => r == !Overlaps(aStart, aEnd, bStart, bEnd), \"Double bookings are refused.\", clause: \"C3\");", "")]
    [InlineData("added requires", "return Spec.Hole<bool>(\"C3\");", "Spec.Requires(aStart > 5, \"extra\");\n        return Spec.Hole<bool>(\"C3\");")]
    [InlineData("code before contracts", "public static bool CanBook(int aStart, int aEnd, int bStart, int bEnd)\n    {\n", "public static bool CanBook(int aStart, int aEnd, int bStart, int bEnd)\n    {\n        var unused = 0;\n")]
    [InlineData("changed return type", "public static bool CanBook(", "public static bool? CanBook(")]
    public void ChangingALockedContractOrSignature_FailsWithVer109(string what, string from, string to)
    {
        var edited = Generated.Replace("return Spec.Hole<bool>(\"C3\");", Filled);
        if (what is "added requires") edited = Generated;
        Assert.Contains(from, edited);
        edited = edited.Replace(from, to);

        var (code, locks) = CheckEdit(edited);
        Assert.NotEqual(0, code);
        var hit = Assert.Single(locks);
        Assert.Equal("Schedule.CanBook", hit.GetProperty("symbol").GetString());
        Assert.Equal("C2,C3", hit.GetProperty("clause").GetString());
        Assert.Contains("Schedule.CanBook", hit.GetProperty("summary").GetString());
    }

    [Fact]
    public void ChangingAParameterType_ReportsTheLockedMethodAsGone()
    {
        var edited = Generated.Replace("public static bool CanBook(int aStart, int aEnd, int bStart, int bEnd)", "public static bool CanBook(int aStart, int aEnd, int bStart, short bEnd)");
        Assert.NotEqual(Generated, edited);
        var (_, locks) = CheckEdit(edited);
        var hit = Assert.Single(locks);
        Assert.Contains("is gone, or its signature changed", hit.GetProperty("summary").GetString());
    }

    [Fact]
    public void BadLockFile_IsAnInputError()
    {
        var dir = Dir();
        var src = Path.Combine(dir, "Generated.cs");
        File.WriteAllText(src, Generated);
        var lockFile = Path.Combine(dir, "bad.contracts.lock.json");
        File.WriteAllText(lockFile, "{\"locks\":[{\"symbol\":\"X\",\"hash\":\"short\"}]}");
        var (code, root) = Json("check", "--file", src, "--lock", lockFile);
        Assert.Equal(3, code);
        Assert.Contains(root.GetProperty("diagnostics").EnumerateArray(), d => d.GetProperty("rule").GetString() == "VER110");
    }

    [Fact]
    public void LockOut_MustUseTheLockSuffix()
    {
        var dir = Dir();
        var src = Path.Combine(dir, "Generated.cs");
        File.WriteAllText(src, Generated);
        Assert.Equal(3, Json("lock", "--file", src, "--out", Path.Combine(dir, "lock.json")).Code);
    }
}
