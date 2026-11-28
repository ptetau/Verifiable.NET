using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Verifiable.Analyzers.Smt;
using Xunit;

namespace Verifiable.Analyzers.Tests;

/// <summary>Regression tests for issue #33: local functions, overrides, <c>Spec.Implies</c> in code, static readonly fields.</summary>
public class CallSoundnessTests
{
    private static async Task<Diagnostic[]> Run(string source) =>
        (await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableSmtAnalyzer(), new VerifiableGatekeeperAnalyzer())).ToArray();

    private static string Msg(Diagnostic d) => d.GetMessage();

    private static void AssertNoErrors(Diagnostic[] diags) =>
        Assert.DoesNotContain(diags, d => d.Id == "AD0001" || d.Severity == DiagnosticSeverity.Error);

    [Fact]
    public async Task ImpliesInCodeChecksTheConsequentEvenWhenTheAntecedentIsFalse()
    {
        var diags = await Run("""
            using Verifiable.Contracts;
            [Verifiable] public static class C
            {
                public static bool Check(int x)
                {
                    Spec.Requires(x >= 0 && x <= 100, "Small.");
                    return Spec.Implies(x != 0, 10 / x >= 0);
                }
            }
            """);
        Assert.Contains(diags, d => d.Id == "VER102" && Msg(d).Contains("ivision", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ImpliesInCodeWithASafeConsequentIsStillProved()
    {
        var diags = await Run("""
            using Verifiable.Contracts;
            [Verifiable] public static class C
            {
                public static bool Check(int x)
                {
                    Spec.Requires(x >= 1 && x <= 100, "Positive.");
                    Spec.Ensures<bool>(r => r, "Always true.");
                    return Spec.Implies(x != 0, 10 / x >= 0);
                }
            }
            """);
        AssertNoErrors(diags);
    }

    [Fact]
    public async Task ImpliesInASpecificationStillGuardsTheConsequent()
    {
        var diags = await Run("""
            using Verifiable.Contracts;
            [Verifiable] public static class C
            {
                public static int Id(int x)
                {
                    Spec.Requires(x >= 0 && x <= 100, "Small.");
                    Spec.Ensures<int>(r => Spec.Implies(x != 0, 10 / x >= 0), "Guarded in the spec.");
                    return x;
                }
            }
            """);
        AssertNoErrors(diags);
    }

    [Theory]
    [InlineData("static int Tenth(int y) => 10 / y;", "var t = Tenth(x);")]
    [InlineData("int Bump() => x + 1;", "var t = Bump();")]
    public async Task LocalFunctionsAreRejected(string decl, string use)
    {
        var diags = await Run($$"""
            using Verifiable.Contracts;
            [Verifiable] public static class C
            {
                public static int Use(int x)
                {
                    Spec.Requires(x >= 0 && x <= 10, "Small.");
                    {{use}}
                    return x;

                    {{decl}}
                }
            }
            """);
        Assert.Contains(diags, d => d.Id == "VER005" && d.Severity == DiagnosticSeverity.Error && Msg(d).Contains("local function"));
    }

    [Fact]
    public async Task LocalFunctionsOutsideVerifiableCodeAreNotReported()
    {
        var diags = await Run("""
            public static class C
            {
                public static int Use(int x)
                {
                    return Twice(x);
                    static int Twice(int y) => y * 2;
                }
            }
            """);
        Assert.DoesNotContain(diags, d => d.Id == "VER005");
    }

    [Fact]
    public async Task OverridingAContractedVirtualMethodIsRejected()
    {
        var diags = await Run("""
            using Verifiable.Contracts;
            [Verifiable] public record Shape
            {
                public virtual int Area()
                {
                    Spec.Ensures<int>(r => r >= 0, "Never negative.");
                    return 0;
                }
            }
            [Verifiable] public sealed record Neg : Shape
            {
                public override int Area() => -1;
            }
            """);
        Assert.Contains(diags, d => d.Id == "VER101" && d.Severity == DiagnosticSeverity.Error && Msg(d).Contains("Neg.Area") && Msg(d).Contains("Shape.Area"));
    }

    [Fact]
    public async Task OverridingAVirtualMethodWithoutContractsIsAccepted()
    {
        var diags = await Run("""
            using Verifiable.Contracts;
            [Verifiable] public record Shape
            {
                public virtual int Area() => 0;
            }
            [Verifiable] public sealed record Sq : Shape
            {
                public override int Area() => 4;
            }
            """);
        AssertNoErrors(diags);
    }

    [Fact]
    public async Task StaticReadonlyFieldReadsAreTheSameValue()
    {
        var diags = await Run("""
            using Verifiable.Contracts;
            [Verifiable] public static class C
            {
                private static readonly int K = 5;
                public static int Zero(int x)
                {
                    Spec.Requires(x >= 0 && x <= 10, "Small.");
                    Spec.Ensures<int>(r => r == x, "Adds K and takes it away again.");
                    return x + K - K;
                }
            }
            """);
        AssertNoErrors(diags);
    }

    [Fact]
    public async Task DifferentStaticReadonlyFieldsAreNotConflated()
    {
        var diags = await Run("""
            using Verifiable.Contracts;
            [Verifiable] public static class C
            {
                private static readonly int K = 5;
                private static readonly int L = 5;
                public static int Zero(int x)
                {
                    Spec.Requires(x >= 0 && x <= 10, "Small.");
                    Spec.Ensures<int>(r => r == x, "Only true if K == L, which the prover cannot know.");
                    return x + K - L;
                }
            }
            """);
        Assert.Contains(diags, d => d.Id == "VER102");
    }
}
