using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Verifiable.Analyzers.Smt;
using Xunit;

namespace Verifiable.Analyzers.Tests;

/// <summary>
/// <c>[Verifiable]</c> code is sound by default (#35): <c>int</c> and <c>long</c> arithmetic must not overflow unless
/// the code says it may wrap, with <c>unchecked(...)</c> or <c>[UncheckedArithmetic]</c>. The nearest
/// <c>[CheckedArithmetic]</c> or <c>[UncheckedArithmetic]</c> (method, enclosing types, assembly) decides.
/// </summary>
public class SoundByDefaultTests
{
    private static async Task<List<Diagnostic>> AnalyzeAsync(string source)
    {
        var diags = await AnalyzerTestHarness.AnalyzeAsync("using System;\nusing Verifiable.Contracts;\n" + source, new VerifiableSmtAnalyzer());
        Assert.DoesNotContain(diags, d => d.Id is "VER101" or "VER103" or "VER105" or "AD0001");
        return diags.Where(d => d.Id == "VER102").ToList();
    }

    private static async Task AssertOverflowRefutedAsync(string source)
    {
        var errors = await AnalyzeAsync(source);
        var overflow = Assert.Single(errors);
        Assert.Contains("Overflow", overflow.GetMessage(), System.StringComparison.Ordinal);
    }

    private static async Task AssertProvedAsync(string source) =>
        Assert.Empty((await AnalyzeAsync(source)).Select(d => d.GetMessage()));

    // ---- overflow is rejected by default ----

    [Theory]
    [InlineData("int", "x + 1")]
    [InlineData("int", "x - 1")]
    [InlineData("int", "x * 2")]
    [InlineData("int", "-x")]
    [InlineData("long", "x + 1L")]
    [InlineData("long", "x - 1L")]
    [InlineData("long", "x * 2L")]
    [InlineData("long", "-x")]
    public async Task UnboundedArithmetic_IsRejectedByDefault(string type, string expression)
    {
        await AssertOverflowRefutedAsync($$"""
            [Verifiable] public static class A
            {
                public static {{type}} F({{type}} x) { Spec.Ensures<{{type}}>(r => r == r, "any"); return {{expression}}; }
            }
            """);
    }

    [Theory]
    [InlineData("int", "x + 1", "x >= 0 && x <= 1000")]
    [InlineData("int", "x - 1", "x >= 0 && x <= 1000")]
    [InlineData("int", "x * 2", "x >= -1000 && x <= 1000")]
    [InlineData("int", "-x", "x > int.MinValue")]
    [InlineData("int", "x + 1", "x < int.MaxValue")]
    [InlineData("long", "x + 1L", "x >= 0L && x <= 1000L")]
    [InlineData("long", "x - 1L", "x > long.MinValue")]
    [InlineData("long", "x * 2L", "x >= -1000L && x <= 1000L")]
    [InlineData("long", "-x", "x > long.MinValue")]
    public async Task BoundedArithmetic_IsAccepted(string type, string expression, string bound)
    {
        await AssertProvedAsync($$"""
            [Verifiable] public static class A
            {
                public static {{type}} F({{type}} x) { Spec.Requires({{bound}}, "bounded"); Spec.Ensures<{{type}}>(r => r == {{expression}}, "exact"); return {{expression}}; }
            }
            """);
    }

    [Fact]
    public async Task TheBoundMustReallyRuleOutOverflow()
    {
        await AssertOverflowRefutedAsync("""
            [Verifiable] public static class A
            {
                public static int F(int x) { Spec.Requires(x >= 0 && x <= int.MaxValue, "not enough"); return x + 1; }
            }
            """);
    }

    [Fact]
    public async Task SumOfTwoBoundedValues_NeedsBothBounds()
    {
        await AssertProvedAsync("""
            [Verifiable] public static class A
            {
                public static int F(int a, int b) { Spec.Requires(a >= 0 && a <= 1000 && b >= 0 && b <= 1000, "bounded"); return a + b; }
            }
            """);
        await AssertOverflowRefutedAsync("""
            [Verifiable] public static class A
            {
                public static int F(int a, int b) { Spec.Requires(a >= 0 && a <= 1000, "only a"); return a + b; }
            }
            """);
    }

    [Fact]
    public async Task CompoundAssignmentAndIncrement_AreChecked()
    {
        await AssertOverflowRefutedAsync("""
            [Verifiable] public static class A
            {
                public static int F(int x) { Spec.Requires(x >= 0, "non-negative"); x += 1; return x; }
            }
            """);
        await AssertOverflowRefutedAsync("""
            [Verifiable] public static class A
            {
                public static int F(int x) { Spec.Requires(x >= 0, "non-negative"); x++; return x; }
            }
            """);
        await AssertProvedAsync("""
            [Verifiable] public static class A
            {
                public static int F(int x) { Spec.Requires(x >= 0 && x <= 1000, "bounded"); x += 1; x++; return x; }
            }
            """);
    }

    [Fact]
    public async Task LoopCounter_NeedsABound()
    {
        await AssertProvedAsync("""
            [Verifiable] public static class A
            {
                public static int F(int n)
                {
                    Spec.Requires(n >= 0 && n <= 1000, "bounded");
                    var i = 0;
                    while (i < n) { Spec.LoopInvariant(i >= 0 && i <= n, "inv"); Spec.Decreases(n - i); i++; }
                    return i;
                }
            }
            """);
    }

    // ---- Math.Abs and MinValue / -1 ----

    [Fact]
    public async Task MathAbs_OfMinValue_IsRejected_AndAcceptedWithABound()
    {
        await AssertOverflowRefutedAsync("""
            [Verifiable] public static class A
            {
                public static int F(int x) { Spec.Ensures<int>(r => r >= 0, "never negative"); return Math.Abs(x); }
            }
            """);
        await AssertOverflowRefutedAsync("""
            [Verifiable] public static class A
            {
                public static long F(long x) { Spec.Ensures<long>(r => r >= 0L, "never negative"); return Math.Abs(x); }
            }
            """);
        await AssertProvedAsync("""
            [Verifiable] public static class A
            {
                public static int F(int x) { Spec.Requires(x > int.MinValue, "no MinValue"); Spec.Ensures<int>(r => r >= 0, "never negative"); return Math.Abs(x); }
            }
            """);
    }

    [Fact]
    public async Task MathAbs_OfMinValue_IsRejectedEvenUnderUncheckedArithmetic()
    {
        // Math.Abs(MinValue) throws OverflowException however the project is compiled.
        await AssertOverflowRefutedAsync("""
            [Verifiable, UncheckedArithmetic] public static class A
            {
                public static int F(int x) { Spec.Ensures<int>(r => r >= 0, "never negative"); return Math.Abs(x); }
            }
            """);
    }

    [Fact]
    public async Task MinValueDividedByMinusOne_IsRejected_AndAcceptedWithABound()
    {
        await AssertOverflowRefutedAsync("""
            [Verifiable] public static class A
            {
                public static int F(int x, int y) { Spec.Requires(y != 0, "non-zero"); return x / y; }
            }
            """);
        await AssertOverflowRefutedAsync("""
            [Verifiable] public static class A
            {
                public static long F(long x, long y) { Spec.Requires(y != 0L, "non-zero"); return x % y; }
            }
            """);
        await AssertProvedAsync("""
            [Verifiable] public static class A
            {
                public static int F(int x, int y) { Spec.Requires(y > 0, "positive"); return x / y; }
            }
            """);
    }

    // ---- [UncheckedArithmetic] ----

    [Fact]
    public async Task UncheckedArithmetic_Wraps_SoSuccessorIsNotAlwaysGreater()
    {
        var errors = await AnalyzeAsync("""
            [Verifiable, UncheckedArithmetic] public static class A
            {
                public static int Next(int x) { Spec.Ensures<int>(r => r > x, "grows"); return x + 1; }
            }
            """);
        var failure = Assert.Single(errors);
        Assert.Contains("Postcondition", failure.GetMessage(), System.StringComparison.Ordinal);
        Assert.Contains("2147483647", failure.GetMessage(), System.StringComparison.Ordinal);
    }

    [Fact]
    public async Task UncheckedArithmetic_ProvesWhatWrapAroundGuarantees()
    {
        await AssertProvedAsync("""
            [Verifiable, UncheckedArithmetic] public static class A
            {
                public static int Next(int x)
                {
                    Spec.Ensures<int>(r => (x == int.MaxValue && r == int.MinValue) || r == x + 1, "wraps at the top");
                    return x + 1;
                }

                public static int Negate(int x)
                {
                    Spec.Requires(x == int.MinValue, "pinned");
                    Spec.Ensures<int>(r => r == int.MinValue, "-MinValue wraps to MinValue");
                    return -x;
                }

                public static long Product(long x)
                {
                    Spec.Requires(x == long.MaxValue, "pinned");
                    Spec.Ensures<long>(r => r == -2L, "the product wraps");
                    return x * 2L;
                }

                public static int Half(int x) { Spec.Ensures<int>(r => r == x + x - x, "x + x - x is x"); return x + x - x; }
            }
            """);
    }

    [Fact]
    public async Task UncheckedArithmetic_OnAMethod_OnlyAffectsThatMethod()
    {
        var errors = await AnalyzeAsync("""
            [Verifiable] public static class A
            {
                [UncheckedArithmetic] public static int Wraps(int x) => x + 1;
                public static int Checked(int x) => x + 1;
            }
            """);
        var failure = Assert.Single(errors);
        Assert.Contains("A.Checked", failure.GetMessage(), System.StringComparison.Ordinal);
    }

    [Fact]
    public async Task UncheckedArithmetic_OnAClass_CoversItsMethods_AndNestedTypes()
    {
        await AssertProvedAsync("""
            [Verifiable, UncheckedArithmetic] public static class A
            {
                public static int F(int x) => x + 1;
                public static class Inner { public static int G(int x) => x * x; }
            }
            """);
    }

    [Fact]
    public async Task UncheckedArithmetic_OnTheAssembly_CoversEverything()
    {
        await AssertProvedAsync("""
            [assembly: UncheckedArithmetic]
            [Verifiable] public static class A
            {
                public static int F(int x) => x + 1;
                public static long G(long x) => x * x;
            }
            """);
    }

    // ---- unchecked(...) and checked(...) ----

    [Fact]
    public async Task UncheckedExpression_Wraps_WithoutTheAttribute()
    {
        await AssertProvedAsync("""
            [Verifiable] public static class A
            {
                public static int Next(int x) { Spec.Ensures<int>(r => r != x, "differs"); return unchecked(x + 1); }
            }
            """);

        var errors = await AnalyzeAsync("""
            [Verifiable] public static class A
            {
                public static int Next(int x) { Spec.Ensures<int>(r => r > x, "grows"); return unchecked(x + 1); }
            }
            """);
        Assert.Contains("Postcondition", Assert.Single(errors).GetMessage(), System.StringComparison.Ordinal);
    }

    [Fact]
    public async Task UncheckedBlock_Wraps_ButCodeOutsideItIsStillChecked()
    {
        await AssertOverflowRefutedAsync("""
            [Verifiable] public static class A
            {
                public static int F(int x)
                {
                    int wrapped;
                    unchecked { wrapped = x + 1; }
                    return wrapped + x;
                }
            }
            """);
        await AssertProvedAsync("""
            [Verifiable] public static class A
            {
                public static int F(int x)
                {
                    Spec.Requires(x == int.MaxValue, "pinned");
                    Spec.Ensures<int>(r => r == int.MinValue, "wrapped");
                    unchecked { x = x + 1; }
                    return x;
                }
            }
            """);
    }

    [Fact]
    public async Task CheckedExpression_InsideUncheckedScope_StillChecks()
    {
        await AssertOverflowRefutedAsync("""
            [Verifiable, UncheckedArithmetic] public static class A
            {
                public static int F(int x) => checked(x + 1);
            }
            """);
        await AssertOverflowRefutedAsync("""
            [Verifiable, UncheckedArithmetic] public static class A
            {
                public static int F(int x) { checked { return x * 2; } }
            }
            """);
        await AssertProvedAsync("""
            [Verifiable, UncheckedArithmetic] public static class A
            {
                public static int F(int x) { Spec.Requires(x >= 0 && x <= 1000, "bounded"); return checked(x + 1); }
            }
            """);
    }

    [Fact]
    public async Task UncheckedExpression_InsideCheckedScope_Wraps()
    {
        await AssertProvedAsync("""
            [Verifiable, CheckedArithmetic] public static class A
            {
                public static int F(int x) { Spec.Ensures<int>(r => r != x, "differs"); return unchecked(x + 1); }
            }
            """);
    }

    // ---- the nearest attribute wins ----

    [Fact]
    public async Task NearestAttribute_MethodBeatsClass()
    {
        var errors = await AnalyzeAsync("""
            [Verifiable, UncheckedArithmetic] public static class A
            {
                [CheckedArithmetic] public static int Strict(int x) => x + 1;
                public static int Lax(int x) => x + 1;
            }
            """);
        var failure = Assert.Single(errors);
        Assert.Contains("A.Strict", failure.GetMessage(), System.StringComparison.Ordinal);

        await AssertProvedAsync("""
            [Verifiable, CheckedArithmetic] public static class A
            {
                [UncheckedArithmetic] public static int Lax(int x) { Spec.Ensures<int>(r => r != x, "differs"); return x + 1; }
            }
            """);
    }

    [Fact]
    public async Task NearestAttribute_InnerTypeBeatsOuterType()
    {
        var errors = await AnalyzeAsync("""
            [Verifiable, UncheckedArithmetic] public static class Outer
            {
                public static int Lax(int x) => x + 1;

                [CheckedArithmetic] public static class Inner { public static int Strict(int x) => x + 1; }
            }
            """);
        var failure = Assert.Single(errors);
        Assert.Contains("Inner.Strict", failure.GetMessage(), System.StringComparison.Ordinal);
    }

    [Fact]
    public async Task NearestAttribute_TypeBeatsAssembly()
    {
        var errors = await AnalyzeAsync("""
            [assembly: UncheckedArithmetic]
            [Verifiable] public static class A
            {
                public static int Lax(int x) => x + 1;
            }
            [Verifiable, CheckedArithmetic] public static class B
            {
                public static int Strict(int x) => x + 1;
            }
            """);
        var failure = Assert.Single(errors);
        Assert.Contains("B.Strict", failure.GetMessage(), System.StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckedArithmeticAttribute_StillMeansBitVectors()
    {
        // Bit-vector arithmetic wraps in the model, so an overflowing computation is rejected, not given a wrong value.
        await AssertOverflowRefutedAsync("""
            [Verifiable, CheckedArithmetic] public static class A
            {
                public static int F(int x) => x + 1;
            }
            """);
        await AssertProvedAsync("""
            [Verifiable, CheckedArithmetic] public static class A
            {
                public static int F(int x) { Spec.Requires(x >= 0 && x <= 1000, "bounded"); return x + 1; }
            }
            """);
    }

    // ---- uint and ulong ----

    [Fact]
    public async Task UnsignedSubtraction_UnderflowIsRejected_AndAcceptedWithABound()
    {
        await AssertOverflowRefutedAsync("""
            [Verifiable] public static class A
            {
                public static uint F(uint x) => x - 1u;
            }
            """);
        await AssertOverflowRefutedAsync("""
            [Verifiable] public static class A
            {
                public static ulong F(ulong x) => x - 1UL;
            }
            """);
        await AssertProvedAsync("""
            [Verifiable] public static class A
            {
                public static uint F(uint x) { Spec.Requires(x >= 1u, "positive"); Spec.Ensures<uint>(r => r < x, "smaller"); return x - 1u; }
            }
            """);
    }

    [Fact]
    public async Task UnsignedAddition_OverflowAtTheTopIsRejected_AndAcceptedWithABound()
    {
        await AssertOverflowRefutedAsync("""
            [Verifiable] public static class A
            {
                public static uint F(uint x) => x + 1u;
            }
            """);
        await AssertOverflowRefutedAsync("""
            [Verifiable] public static class A
            {
                public static ulong F(ulong x, ulong y) { Spec.Requires(x <= 18446744073709551615UL, "any"); return x * y; }
            }
            """);
        await AssertProvedAsync("""
            [Verifiable] public static class A
            {
                public static uint F(uint x) { Spec.Requires(x < 4294967295u, "room"); Spec.Ensures<uint>(r => r > x, "grows"); return x + 1u; }
            }
            """);
    }

    [Fact]
    public async Task UnsignedUnderUncheckedArithmetic_WrapsAroundTheTypeRange()
    {
        await AssertProvedAsync("""
            [Verifiable, UncheckedArithmetic] public static class A
            {
                public static uint Dec(uint x)
                {
                    Spec.Requires(x == 0u, "pinned");
                    Spec.Ensures<uint>(r => r == 4294967295u, "0u - 1u is uint.MaxValue");
                    return x - 1u;
                }

                public static ulong Inc(ulong x)
                {
                    Spec.Requires(x == 18446744073709551615UL, "pinned");
                    Spec.Ensures<ulong>(r => r == 0UL, "ulong.MaxValue + 1 is 0");
                    return x + 1UL;
                }
            }
            """);

        var errors = await AnalyzeAsync("""
            [Verifiable, UncheckedArithmetic] public static class A
            {
                public static uint Dec(uint x) { Spec.Ensures<uint>(r => r < x, "smaller"); return x - 1u; }
            }
            """);
        Assert.Contains("Postcondition", Assert.Single(errors).GetMessage(), System.StringComparison.Ordinal);
    }

    // ---- inputs are real C# values ----

    [Fact]
    public async Task Parameters_AreAssumedToBeInRangeOfTheirType()
    {
        // No integer above int.MaxValue exists, so a bound just under it pins x exactly.
        await AssertProvedAsync("""
            [Verifiable] public static class A
            {
                public static int F(int x) { Spec.Requires(x > 2147483646, "top"); Spec.Ensures<int>(r => r == int.MaxValue, "exactly the maximum"); return x; }
            }
            """);
    }
}
