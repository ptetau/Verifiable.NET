using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Verifiable.Query.Tests;

/// <summary>
/// Requirement R4: <c>Spec.Hole</c> marks a body the LLM still has to write. It compiles, fails the build
/// with VER108 naming the clause, is listed as a work item, and is never treated as proved.
/// </summary>
[Collection("CurrentDirectory")]
public sealed class HoleTests
{
    private const string Source = """
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

            public static void Notify(int bookingId)
            {
                Spec.Requires(bookingId > 0, "Bookings have positive IDs.", clause: "C4");
                Spec.Hole("C4");
            }
        }
        """;

    private const string Manifest = """
        {"clauses":[{"id":"C2","text":"A booking ends after it starts."},{"id":"C3","text":"Double bookings are refused."},{"id":"C4","text":"Guests are told."}]}
        """;

    private static (string Src, string Manifest) Setup(string source)
    {
        var dir = Path.Combine(Path.GetTempPath(), "vq-hole-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var src = Path.Combine(dir, "Schedule.cs");
        var man = Path.Combine(dir, "spec.clauses.json");
        File.WriteAllText(src, source);
        File.WriteAllText(man, Manifest);
        return (src, man);
    }

    private static (int Code, string Out) Run(params string[] args)
    {
        using var o = new StringWriter();
        using var e = new StringWriter();
        var code = Program.Run(args, null, o, e);
        return (code, o.ToString() + e.ToString());
    }

    private static (int Code, JsonElement Root) Json(params string[] args)
    {
        var (code, output) = Run(args.Append("--format").Append("json").ToArray());
        return (code, JsonDocument.Parse(output).RootElement.Clone());
    }

    [Fact]
    public void Hole_FailsTheBuildWithItsOwnDiagnosticNamingTheClause()
    {
        var (src, _) = Setup(Source);
        var (code, root) = Json("check", "--file", src);

        Assert.Equal(1, code);
        var diags = root.GetProperty("diagnostics").EnumerateArray().ToList();
        Assert.All(diags, d => Assert.Equal("VER108", d.GetProperty("rule").GetString()));
        Assert.Equal(2, diags.Count);

        var canBook = diags.Single(d => d.GetProperty("symbol").GetString() == "Schedule.CanBook");
        Assert.Equal("error", canBook.GetProperty("severity").GetString());
        Assert.Equal("C3", canBook.GetProperty("clause").GetString());
        Assert.Contains("Spec.Ensures<bool>", canBook.GetProperty("contract").GetString());
        Assert.Contains("C3", canBook.GetProperty("summary").GetString());

        var notify = diags.Single(d => d.GetProperty("symbol").GetString() == "Schedule.Notify");
        Assert.Equal("C4", notify.GetProperty("clause").GetString());
    }

    [Fact]
    public void AgentMode_EveryHoleCarriesAJsonWorkItemWithClauseText()
    {
        var (src, man) = Setup(Source);
        var old = Environment.GetEnvironmentVariable("VERIFIABLE_AGENT");
        try
        {
            Environment.SetEnvironmentVariable("VERIFIABLE_AGENT", "1");
            var (_, output) = Run("check", "--file", src, "--clauses", man);
            var items = Regex.Matches(output, "<verifiable-hole>(\\{[^\\n]*?\\})</verifiable-hole>")
                .Select(m => JsonDocument.Parse(m.Groups[1].Value).RootElement.Clone())
                .ToList();
            var canBook = items.First(i => i.GetProperty("symbol").GetString() == "Schedule.CanBook");
            Assert.Equal("C3", canBook.GetProperty("clause").GetString());
            Assert.Equal("Double bookings are refused.", canBook.GetProperty("clauseText").GetString());
            Assert.Equal("public static bool CanBook(int aStart, int aEnd, int bStart, int bEnd)", canBook.GetProperty("signature").GetString());
            Assert.Equal(2, canBook.GetProperty("contracts").GetArrayLength());
        }
        finally
        {
            Environment.SetEnvironmentVariable("VERIFIABLE_AGENT", old);
        }
    }

    [Fact]
    public void HolesCommand_ListsTheWorkList()
    {
        var (src, man) = Setup(Source);
        var (code, root) = Json("holes", "--file", src, "--clauses", man);

        Assert.Equal(1, code);
        Assert.Equal("verifiable-query/holes@1", root.GetProperty("schema").GetString());
        var holes = root.GetProperty("holes").EnumerateArray().ToList();
        Assert.Equal(new[] { "Schedule.CanBook", "Schedule.Notify" }, holes.Select(h => h.GetProperty("symbol").GetString()).ToArray());
        Assert.Equal("Guests are told.", holes[1].GetProperty("clauseText").GetString());
        Assert.Equal("public static void Notify(int bookingId)", holes[1].GetProperty("signature").GetString());
    }

    [Fact]
    public void Holes_AreNeverTreatedAsProved()
    {
        var (src, _) = Setup(Source);
        var (code, root) = Json("ask", "--file", src, "--target", "Schedule.CanBook", "--assert", "result == true || result == false");
        Assert.Equal(2, code);
        Assert.Equal("undecided", root.GetProperty("status").GetString());
        Assert.Contains("Spec.Hole", root.GetProperty("results")[0].GetProperty("error").GetString());
    }

    [Fact]
    public void FillingTheHole_IsThenProvedOrRefutedLikeAnyBody()
    {
        var right = Source
            .Replace("return Spec.Hole<bool>(\"C3\");", "return !Overlaps(aStart, aEnd, bStart, bEnd);")
            .Replace("Spec.Hole(\"C4\");", string.Empty);
        var (rightSrc, _) = Setup(right);
        var ok = Json("check", "--file", rightSrc);
        Assert.Equal(0, ok.Code);
        Assert.Equal(0, Json("holes", "--file", rightSrc).Code);

        var wrong = right.Replace("return !Overlaps(aStart, aEnd, bStart, bEnd);", "return Overlaps(aStart, aEnd, bStart, bEnd);");
        var (wrongSrc, _) = Setup(wrong);
        var bad = Json("check", "--file", wrongSrc);
        Assert.Equal(1, bad.Code);
        var failure = Assert.Single(bad.Root.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("VER102", failure.GetProperty("rule").GetString());
        Assert.Equal("C3", failure.GetProperty("clause").GetString());
    }
}
