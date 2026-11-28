using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Verifiable.Query.Tests;

/// <summary>
/// Requirement R5: for each procedure, Z3 searches for an input where two different results both meet
/// the contract. Fixed means any body that passes is correct; open comes with the input and both results.
/// </summary>
[Collection("CurrentDirectory")]
public sealed class OneAnswerTests
{
    private const string Source = """
        using Verifiable.Contracts;

        [Verifiable]
        public static class Rates
        {
            public static int Fixed(int nights)
            {
                Spec.Requires(nights >= 1 && nights <= 30, "Stays are short.", clause: "C1");
                Spec.Ensures<int>(r => r == nights * 100, "A night costs 100.", clause: "C1");
                return nights * 100;
            }

            public static int Weak(int nights)
            {
                Spec.Requires(nights >= 1 && nights <= 30, "Stays are short.", clause: "C2");
                Spec.Ensures<int>(r => r >= nights * 90 && r <= nights * 100, "Prices are within the band.", clause: "C2");
                return nights * 100;
            }

            [AnyValidResult("Any slot in range is fine.")]
            public static int AnySlot(int n)
            {
                Spec.Requires(n >= 1 && n <= 10, "Small.");
                Spec.Ensures<int>(r => r >= 0 && r < n, "A slot index.");
                return 0;
            }

            public static int NoPost(int x)
            {
                Spec.Requires(x >= 0 && x <= 10, "Small.");
                return x;
            }

            public static bool Overlaps(int a1, int a2, int b1, int b2)
            {
                Spec.Requires(a1 < a2 && b1 < b2, "Ranges are ordered.");
                Spec.EnsuresWhen<bool>(a1 < b2 && b1 < a2, r => r, "Overlapping ranges overlap.");
                Spec.EnsuresWhen<bool>(!(a1 < b2 && b1 < a2), r => !r, "Disjoint ranges do not.");
                return a1 < b2 && b1 < a2;
            }
        }
        """;

    private static (int Code, JsonElement Root) Json(params string[] args)
    {
        var dir = Path.Combine(Path.GetTempPath(), "vq-one-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var src = Path.Combine(dir, "Rates.cs");
        File.WriteAllText(src, Source);
        using var o = new StringWriter();
        var code = Program.Run(args.Select(a => a.Replace("{src}", src)).Append("--format").Append("json").ToArray(), null, o, new StringWriter());
        return (code, JsonDocument.Parse(o.ToString()).RootElement.Clone());
    }

    [Fact]
    public void ReportsEachProcedureAsFixedOpenOrInconclusive()
    {
        var (code, root) = Json("one-answer", "--file", "{src}");
        Assert.Equal(1, code);
        Assert.Equal("verifiable-query/one-answer@1", root.GetProperty("schema").GetString());
        var byName = root.GetProperty("methods").EnumerateArray().ToDictionary(m => m.GetProperty("symbol").GetString()!);

        Assert.Equal("fixed", byName["Rates.Fixed"].GetProperty("verdict").GetString());
        Assert.Equal("fixed", byName["Rates.Overlaps"].GetProperty("verdict").GetString());

        var weak = byName["Rates.Weak"];
        Assert.Equal("open", weak.GetProperty("verdict").GetString());
        Assert.Equal(new[] { "C2" }, weak.GetProperty("clauses").EnumerateArray().Select(c => c.GetString()).ToArray());
        var ce = weak.GetProperty("counterexample");
        var nights = int.Parse(ce.GetProperty("input").GetProperty("nights").GetString()!);
        var r1 = int.Parse(ce.GetProperty("result1").GetString()!);
        var r2 = int.Parse(ce.GetProperty("result2").GetString()!);
        Assert.NotEqual(r1, r2);
        foreach (var r in new[] { r1, r2 })
            Assert.True(r >= nights * 90 && r <= nights * 100, $"{r} must meet the contract for nights = {nights}");

        Assert.Equal("open", byName["Rates.NoPost"].GetProperty("verdict").GetString());
        Assert.Contains("no postcondition", byName["Rates.NoPost"].GetProperty("note").GetString());
    }

    [Fact]
    public void AnyValidResult_RecordsThatMoreThanOneAnswerIsFine()
    {
        var (code, root) = Json("one-answer", "--file", "{src}", "--target", "Rates.AnySlot,Rates.Fixed");
        Assert.Equal(0, code);
        var slot = root.GetProperty("methods").EnumerateArray().Single(m => m.GetProperty("symbol").GetString() == "Rates.AnySlot");
        Assert.Equal("open", slot.GetProperty("verdict").GetString());
        Assert.True(slot.GetProperty("anyValidResult").GetBoolean());
        Assert.Equal("Any slot in range is fine.", slot.GetProperty("anyValidReason").GetString());
    }

    [Fact]
    public void CheckOption_MakesUnmarkedOpenProceduresErrors()
    {
        var (code, root) = Json("check", "--file", "{src}", "--one-answer");
        Assert.Equal(1, code);
        var flagged = root.GetProperty("diagnostics").EnumerateArray()
            .Where(d => d.GetProperty("rule").GetString() == "VER111")
            .Select(d => d.GetProperty("symbol").GetString()).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "Rates.NoPost", "Rates.Weak" }, flagged);

        var (plainCode, plain) = Json("check", "--file", "{src}");
        Assert.Equal(0, plainCode);
        Assert.DoesNotContain(plain.GetProperty("diagnostics").EnumerateArray(), d => d.GetProperty("rule").GetString() == "VER111");
    }

    [Fact]
    public void ResultsThatCannotBeCompared_AreInconclusive()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vq-one-cmp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var src = Path.Combine(dir, "Cmp.cs");
        File.WriteAllText(src, """
            using Verifiable.Contracts;

            public readonly struct Pair { public Pair(int a) { A = a; } public int A { get; } }

            [Verifiable]
            public static class Cmp
            {
                public static Pair Make(int a)
                {
                    Spec.Requires(a >= 0 && a <= 10, "Small.");
                    Spec.Ensures<Pair>(r => r.A == a, "Keeps the value.");
                    return new Pair(a);
                }
            }
            """);
        using var o = new StringWriter();
        var code = Program.Run(["one-answer", "--file", src, "--format", "json"], null, o, new StringWriter());
        var m = JsonDocument.Parse(o.ToString()).RootElement.GetProperty("methods")[0];
        Assert.Equal("inconclusive", m.GetProperty("verdict").GetString());
        Assert.Contains("cannot be compared", m.GetProperty("note").GetString());
        Assert.Equal(2, code);
    }
}
