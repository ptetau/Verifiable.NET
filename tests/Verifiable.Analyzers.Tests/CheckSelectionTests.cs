using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Verifiable.Analyzers.Smt;
using Xunit;

namespace Verifiable.Analyzers.Tests;

/// <summary>
/// Every <c>[Verifiable]</c> method is lowered, and only what lowering finds decides whether it is checked.
/// These methods have no contracts. A source pre-filter used to skip them, so their obligations never
/// reached Z3 and they looked proved.
/// </summary>
public class CheckSelectionTests
{
    private const string Types = """
        using Verifiable.Contracts;
        namespace Selection;

        [Verifiable]
        public readonly record struct Pct(int V)
        {
            [Invariant("A percentage is between 0 and 100.")]
            public bool InRange => V >= 0 && V <= 100;
        }

        [Verifiable]
        public sealed class Box
        {
            public int Size { get; }
            public Box(int size)
            {
                Spec.Requires(size > 0, "A box has a positive size.");
                Size = size + 0;
            }
        }

        [Verifiable]
        public sealed class Crate
        {
            public int Size { get; }
            public Crate(int size)
            {
                Size = size + 0;
            }
        }

        """;

    [Theory]
    [InlineData("public static int Div(int a, int b) => a / b;", "Div")]
    [InlineData("public static int Rem(int a, int b) => a % b;", "Rem")]
    [InlineData("public static decimal Ratio(decimal a, decimal b) => a / b;", "Ratio")]
    public async Task DivisionWithoutContracts_IsChecked(string member, string name)
    {
        var diagnostics = await AnalyzeAsync(member);

        Assert.Contains(diagnostics, d => d.Id == "VER102" && d.GetMessage().Contains($"DivisionByZero of P.{name} fails", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GuardedDivisionWithoutContracts_IsProved()
    {
        var diagnostics = await AnalyzeAsync("public static int Div(int a, int b) => b == 0 ? 0 : a / b;");

        Assert.Empty(diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString()));
    }

    [Fact]
    public async Task TupleReturnWithInvariantElement_IsChecked()
    {
        var diagnostics = await AnalyzeAsync("public static (Pct P, int N) Pair() => (new Pct(150), 1);");

        Assert.Contains(diagnostics, d => d.Id == "VER102" && d.GetMessage().Contains("TypeInvariant of P.Pair fails", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NamedTupleReturnFromUnnamedTuple_IsProved()
    {
        // `(p, n)` has an unnamed tuple type and the return type is named. They lower to different sorts, which
        // used to give Z3 a sort error (VER103) once such methods were checked.
        var diagnostics = await AnalyzeAsync("""
            public static (Pct P, int N) Pair(Pct p, int n)
            {
                Spec.Ensures<(Pct P, int N)>(r => r.N == n && r.P.V == p.V, "The pair holds the inputs.");
                return (p, n);
            }

            public static (Pct P, int N) Plain(Pct p, int n) => (p, n);
            """);

        Assert.Empty(diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString()));
    }

    [Fact]
    public async Task MethodWithNothingToProve_ReportsNothing()
    {
        var diagnostics = await AnalyzeAsync("public static int Add(int a, int b) => a + b;");

        Assert.Empty(diagnostics.Select(d => d.ToString()));
    }

    [Fact]
    public async Task UntranslatableCallToConstructorWithRequires_IsAnError()
    {
        // Box's constructor has a precondition, but its body cannot be lowered, so the call cannot be checked.
        var diagnostics = await AnalyzeAsync("public static Box Make() => new Box(0);");

        Assert.Contains(diagnostics, d => d.Id == "VER101" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task UntranslatableMethodWithoutContracts_IsAWarningNotSilence()
    {
        var diagnostics = await AnalyzeAsync("public static Crate Make() => new Crate(1);");

        var notChecked = Assert.Single(diagnostics, d => d.Id == "VER101");
        Assert.Equal(DiagnosticSeverity.Warning, notChecked.Severity);
        Assert.Contains("P.Make was not checked", notChecked.GetMessage(), StringComparison.Ordinal);
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("public static int Div(int a, int b) => a / b;", "P.Div", DiagnosticSeverity.Error)]
    [InlineData("public static int Same(int a) => a;", "P.Same", DiagnosticSeverity.Warning)]
    public async Task InternalLoweringFailure_IsReported_NotAnAnalyzerCrash(string member, string display, DiagnosticSeverity expected)
    {
        Verifiable.Analyzers.Smt.CfgSsaLowerer.SimulateInternalFailure.Value = true;
        try
        {
            var diagnostics = await AnalyzeAsync(member);

            Assert.DoesNotContain(diagnostics, d => d.Id == "AD0001");
            // The shared test types are lowered too and fail the same way; look at the method under test.
            var failure = Assert.Single(diagnostics, d => d.Id == "VER101" && d.GetMessage().Contains(display, StringComparison.Ordinal));
            Assert.Equal(expected, failure.Severity);
            Assert.Contains("Internal error while translating", failure.GetMessage(), StringComparison.Ordinal);
        }
        finally
        {
            Verifiable.Analyzers.Smt.CfgSsaLowerer.SimulateInternalFailure.Value = false;
        }
    }

    private static Task<System.Collections.Immutable.ImmutableArray<Diagnostic>> AnalyzeAsync(string members) =>
        AnalyzerTestHarness.AnalyzeAsync(
            Types + "[Verifiable]\npublic static class P\n{\n" + members + "\n}\n",
            new VerifiableSmtAnalyzer());
}
