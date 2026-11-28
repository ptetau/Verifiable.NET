using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Verifiable.Analyzers.Smt;
using Xunit;

namespace Verifiable.Analyzers.Tests;

/// <summary>
/// decimal arithmetic, Math.DivRem and enum arithmetic throw or wrap like C# does (#50): decimal overflow always throws,
/// DivRem has the same obligations as / and %, and an enum is computed in its underlying type.
/// </summary>
public class OverflowCoverageTests
{
    private static async Task<List<string>> AnalyzeAsync(string source)
    {
        var diags = await AnalyzerTestHarness.AnalyzeAsync("using System;\nusing Verifiable.Contracts;\n" + source, new VerifiableSmtAnalyzer());
        var problems = diags.Where(d => d.Id is "VER101" or "VER103" or "VER105" or "AD0001").Select(d => d.Id + ": " + d.GetMessage()).ToList();
        Assert.True(problems.Count == 0, string.Join("\n", problems));
        return diags.Where(d => d.Id == "VER102").Select(d => d.GetMessage()).ToList();
    }

    private static async Task AssertProvedAsync(string source) => Assert.True((await AnalyzeAsync(source)) is { Count: 0 } l, string.Join("\n", await AnalyzeAsync(source)));

    private static async Task AssertRefutedAsync(string source, string? containing = null)
    {
        var errors = await AnalyzeAsync(source);
        Assert.NotEmpty(errors);
        if (containing is not null)
            Assert.Contains(errors, e => e.Contains(containing, System.StringComparison.Ordinal));
    }

    private static string Wrap(string attribute, string method) => $$"""
        [Verifiable] {{attribute}}
        public static class A
        {
            {{method}}
        }
        """;

    public static TheoryData<string> AllModes() => new(["", "[CheckedArithmetic]", "[UncheckedArithmetic]"]);

    [Theory, MemberData(nameof(AllModes))]
    public async Task DecimalAdd_WithoutABound_CanOverflow(string mode) =>
        await AssertRefutedAsync(Wrap(mode, "public static decimal F(decimal d) { Spec.Requires(d >= 0m, \"x\"); return d + 1m; }"), "decimal");

    [Theory, MemberData(nameof(AllModes))]
    public async Task DecimalOps_WithABound_AreProved(string mode) =>
        await AssertProvedAsync(Wrap(mode, """
            public static decimal F(decimal d)
            {
                Spec.Requires(d >= 0m && d <= 1000m, "bounded");
                Spec.Ensures<decimal>(r => r == d * 2m + 1m - 3m, "exact");
                return d * 2m + 1m - 3m;
            }
            public static decimal Half(decimal d, decimal e)
            {
                Spec.Requires(e >= 1m, "not tiny");
                return d / e;
            }
            """));

    [Theory, MemberData(nameof(AllModes))]
    public async Task DecimalMultiply_CanOverflow(string mode) =>
        await AssertRefutedAsync(Wrap(mode, "public static decimal F(decimal d, decimal e) { Spec.Requires(d > 0m && e > 0m, \"x\"); return d * e; }"), "decimal");

    [Fact]
    public async Task DecimalDivide_ByATinyValue_CanOverflow() =>
        await AssertRefutedAsync(Wrap("", "public static decimal F(decimal d, decimal e) { Spec.Requires(d > 1m && e > 0m, \"x\"); return d / e; }"), "decimal");

    [Fact]
    public async Task DecimalAdd_Helper_CanOverflow() =>
        await AssertRefutedAsync(Wrap("", "public static decimal F(decimal d) { Spec.Requires(d >= 0m, \"x\"); return decimal.Add(d, 1m); }"), "decimal");

    [Theory, MemberData(nameof(AllModes))]
    public async Task DivRem_OfMinValueAndMinusOne_Throws(string mode) =>
        await AssertRefutedAsync(Wrap(mode, "public static int F(int x, int y) { Spec.Requires(y != 0, \"x\"); var (q, _) = Math.DivRem(x, y); return q; }"), "MinValue");

    [Theory, MemberData(nameof(AllModes))]
    public async Task DivRem_ByZero_IsRefuted(string mode) =>
        await AssertRefutedAsync(Wrap(mode, "public static int F(int x, int y) { Spec.Requires(y > 0, \"x\"); var (q, r) = Math.DivRem(x, y); return q + r - r; }") + "\n" +
            "[Verifiable] public static class B { public static long G(long x, long y) { var (q, _) = Math.DivRem(x, y); return q; } }");

    [Theory, MemberData(nameof(AllModes))]
    public async Task DivRem_WithASafeDivisor_IsProvedAndMatchesDivisionAndRemainder(string mode) =>
        await AssertProvedAsync(Wrap(mode, """
            public static int F(int x, int y)
            {
                Spec.Requires(y > 0, "positive");
                Spec.Ensures<int>(r => r == x / y, "quotient");
                var (q, _) = Math.DivRem(x, y);
                return q;
            }
            public static int Rem(int x, int y)
            {
                Spec.Requires(y > 0, "positive");
                Spec.Ensures<int>(r => r == x % y, "remainder");
                var (_, rem) = Math.DivRem(x, y);
                return rem;
            }
            """));

    [Theory, MemberData(nameof(AllModes))]
    public async Task ByteEnumAdd_Wraps(string mode)
    {
        const string decl = "public enum Color : byte { A = 0, B = 255 }\n";
        var errors = await AnalyzeAsync(decl + Wrap(mode, "public static int F(byte b) { Spec.Ensures<int>(r => r == b + 1, \"next\"); return (int)((Color)b + 1); }"));
        Assert.NotEmpty(errors);
    }

    [Fact]
    public async Task ByteEnumAdd_WithRoom_IsProved() =>
        await AssertProvedAsync("public enum Color : byte { A = 0, B = 255 }\n" + Wrap("[UncheckedArithmetic]", """
            public static int F(byte b)
            {
                Spec.Requires(b < 255, "room");
                Spec.Ensures<int>(r => r == b + 1, "next");
                return (int)((Color)b + 1);
            }
            public static int Wraps()
            {
                Spec.Ensures<int>(r => r == 0, "wrapped");
                return (int)((Color)255 + 1);
            }
            """));

    [Fact]
    public async Task ByteEnumIncrement_InCheckedScope_MustNotOverflow() =>
        await AssertRefutedAsync("public enum Color : byte { A = 0, B = 255 }\n" + Wrap("[CheckedArithmetic]",
            "public static Color F(Color c) { c++; return c; }"), "enum");

    [Fact]
    public async Task IntEnumSubtract_InUncheckedScope_Wraps() =>
        await AssertProvedAsync("public enum E : int { A = 0 }\n" + Wrap("[UncheckedArithmetic]", """
            public static int F()
            {
                Spec.Ensures<int>(r => r == int.MaxValue, "wrapped");
                return (int)((E)int.MinValue - 1);
            }
            """));
}
