using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Xunit;
using Verifiable.Analyzers.Smt;

namespace Verifiable.Analyzers.Tests;

public class HelperSpecificationTests
{
    [Fact]
    public async Task NormalizedRecordsCanBeComparedInImplementationAndContract()
    {
        await Check("""
            using Verifiable.Contracts;
            [Verifiable] public readonly record struct Pair(decimal? Amount, string? Code)
            {
                static Pair Normalize(Pair p) =>
                    new(p.Amount.HasValue ? p.Amount.Value : 0m, p.Code == "" ? null : p.Code);
                public static bool Different(Pair a, Pair b)
                {
                    Spec.Ensures<bool>(r => r == (Normalize(a) != Normalize(b)));
                    return Normalize(a) != Normalize(b);
                }
            }
            """);
    }

    [Theory]
    [InlineData("d.Left", false)]
    [InlineData("d.Right", true)]
    public async Task ProjectionProvesFieldMappingRatherThanJustIdenticalCalls(string first, bool swapped)
    {
        var source = $$"""
            using Verifiable.Contracts;
            public record Dto(int Left, int Right);
            public readonly record struct State(int A, int B);
            [Verifiable] public static class Adapter
            {
                static State Project(Dto d, bool includeExtras = true) =>
                    new({{first}}, includeExtras ? d.Right : 0);
                public static State Adapt(Dto d)
                {
                    Spec.Ensures<State>(s => s.A == d.Left && s.B == d.Right);
                    return Project(d);
                }
            }
            """;
        await Check(source, swapped ? "VER102" : null);
    }

    [Theory]
    [InlineData("x >= 0", null)]
    [InlineData("true", "VER102")]
    public async Task PreconditionOnSpecOnlyCallIsProvedNotAssumed(string guard, string? failure)
    {
        var source = $$"""
            using Verifiable.Contracts;
            [Verifiable] public static class Rules
            {
                [Ghost] static int Bound(int a = -1, int b = -1, bool enabled = true)
                {
                    Spec.Requires(b >= 0);
                    Spec.Ensures<int>(r => r == (enabled ? b : a));
                    return enabled ? b : a;
                }
                public static int Keep(int x)
                {
                    Spec.Requires({{guard}});
                    Spec.Ensures<int>(r => r == Bound(b: x, a: -1));
                    return x;
                }
            }
            """;
        await Check(source, failure);
    }

    [Fact]
    public async Task OmittedInvalidDefaultStillRequiresProofInUnannotatedCaller()
    {
        await Check("""
            using Verifiable.Contracts;
            using static Verifiable.Contracts.Spec;
            [Verifiable] public static class Rules
            {
                static int Positive(int n = 0) { Requires(n > 0); return n; }
                public static int Run() => Positive();
            }
            """, "VER102");
    }

    [Fact]
    public async Task GhostOutsideCageCanBeReferencedByASpecification()
    {
        await Check("""
            using Verifiable.Contracts;
            public static class Model { [Ghost] public static bool Empty(int n) => n == 0; }
            [Verifiable] public static class Rules
            {
                public static bool Empty(int n)
                {
                    Spec.Ensures<bool>(r => r == Model.Empty(n));
                    return n == 0;
                }
            }
            """);
    }

    [Fact]
    public async Task OptionalDefaultsAndOutOfOrderNamesBindInExecutableAndSpecCalls()
    {
        await Check("""
            using Verifiable.Contracts;
            [Verifiable] public static class Rules
            {
                static int Select(int a = 4, int b = 9, bool first = true) => first ? a : b;
                public static int Run()
                {
                    Spec.Ensures<int>(r => r == 6 && r == Select(b: 8, a: 6));
                    return Select(first: false, b: 6);
                }
            }
            """);
    }

    [Theory]
    [InlineData("return B(x);", "return A(x);")]
    [InlineData("Spec.Requires(B(x)); return true;", "Spec.Requires(A(x)); return true;")]
    public async Task CyclicDefinitionsAndRequiresDiagnoseWithoutSelfJustification(string a, string b)
    {
        await Check($$"""
            using Verifiable.Contracts;
            [Verifiable] public static class Rules
            {
                static bool A(int x) { {{a}} }
                static bool B(int x) { {{b}} }
                public static bool Run(int x) { Spec.Ensures<bool>(r => r == A(x)); return true; }
            }
            """, "VER101");
    }

    private static async Task Check(string source, string? failure = null)
    {
        var diagnostics = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableSmtAnalyzer(), new VerifiableGatekeeperAnalyzer());
        Assert.Empty(diagnostics.Where(d => d.Id == "AD0001" || d.Id == "VER104" || d.Severity == DiagnosticSeverity.Error && d.Id != failure));
        if (failure is not null) Assert.Contains(diagnostics, d => d.Id == failure);
    }
}
