using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Verifiable.Analyzers.Smt;
using Xunit;

namespace Verifiable.Analyzers.Tests;

public class MapToVerificationTests
{
    [Fact]
    public async Task D13_TypeLevelMapTo_GeneratesVerifiedProjectionWithoutHandWrittenEnsures()
    {
        const string source = """
            using Verifiable.Contracts;

            namespace Sample;

            [MapTo(typeof(OrderState))]
            [MapTo(typeof(OrderSummary), MethodName = "ToSummary")]
            public class OrderDto
            {
                public int Id { get; set; }
                public decimal Total { get; set; }
                public string? Notes { get; set; }
            }

            public readonly record struct OrderState(int Id, decimal Total);

            public readonly record struct OrderSummary
            {
                public int Id { get; init; }
                public string? Notes { get; init; }
            }

            [Verifiable]
            public static class OrderPipeline
            {
                public static OrderState Adapt(OrderDto dto)
                {
                    Spec.Ensures<OrderState>(s => s.Id == dto.Id && s.Total == dto.Total);
                    return dto.ToOrderState();
                }

                public static OrderSummary Summarize(OrderDto dto)
                {
                    Spec.Ensures<OrderSummary>(s => s.Id == dto.Id && s.Notes == dto.Notes);
                    return dto.ToSummary();
                }
            }
            """;

        var diags = await AnalyzerTestHarness.AnalyzeAsync(
            source,
            new VerifiableGatekeeperAnalyzer(),
            new VerifiableSmtAnalyzer());
        Assert.Empty(diags.Where(d => d.Id == "AD0001" || d.Id == "VER104" || d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public async Task D16_MethodLevelMapTo_MapsMutableBoundaryDtosAndCatchesFalsePostcondition()
    {
        const string validSource = """
            using Verifiable.Contracts;

            namespace Sample;

            public class OrderDto { public int Id { get; set; } public decimal Total { get; set; } }
            public class OrderResponse { public int Id { get; set; } public decimal Total { get; set; } }

            [Verifiable]
            public static partial class OrderMapper
            {
                [MapTo]
                public static partial OrderResponse ToResponse(OrderDto dto);

                public static OrderResponse Handle(OrderDto dto)
                {
                    Spec.Ensures<OrderResponse>(r => r.Id == dto.Id && r.Total == dto.Total);
                    return ToResponse(dto);
                }
            }
            """;

        var okDiags = await AnalyzerTestHarness.AnalyzeAsync(
            validSource,
            new VerifiableGatekeeperAnalyzer(),
            new VerifiableSmtAnalyzer());
        Assert.Empty(okDiags.Where(d => d.Id == "AD0001" || d.Id == "VER104" || d.Severity == DiagnosticSeverity.Error));

        var badSource = validSource.Replace("r.Total == dto.Total", "r.Total == dto.Total + 1m");
        var badDiags = await AnalyzerTestHarness.AnalyzeAsync(
            badSource,
            new VerifiableGatekeeperAnalyzer(),
            new VerifiableSmtAnalyzer());
        Assert.DoesNotContain(badDiags, d => d.Id == "AD0001" || d.Id == "VER104");
        Assert.Contains(badDiags, d => d.Id == "VER102");
    }

    [Fact]
    public async Task MapTo_TargetWithInvariant_IsRejectedWithVER203ToPreserveSoundness()
    {
        const string source = """
            using Verifiable.Contracts;

            namespace Sample;

            [MapTo(typeof(NonEmptyRange))]
            public class RawRangeDto
            {
                public int Start { get; set; }
                public int End { get; set; }
            }

            [Verifiable]
            public readonly record struct NonEmptyRange(int Start, int End)
            {
                [Invariant("Start must not exceed End.")]
                public bool IsOrdered => Start <= End;
            }
            """;

        var diags = await AnalyzerTestHarness.AnalyzeAsync(
            source,
            new VerifiableGatekeeperAnalyzer(),
            new VerifiableSmtAnalyzer());
        Assert.DoesNotContain(diags, d => d.Id == "AD0001" || d.Id == "VER104");
        Assert.Contains(diags, d => d.Id == "VER203" && d.GetMessage().Contains("IsOrdered") && d.GetMessage().Contains("[InboundPort]"));
    }

    [Fact]
    public async Task MapTo_GeneratedProjection_WorksAcrossAssemblyBoundaries()
    {
        var libRef = AnalyzerTestHarness.CompileLibraryReference("""
            using Verifiable.Contracts;

            namespace SharedDtos;

            [MapTo(typeof(OrderState))]
            public class OrderDto
            {
                public int Id { get; set; }
                public decimal Total { get; set; }
            }

            public readonly record struct OrderState(int Id, decimal Total);
            """, "SharedMapToLib");

        const string consumer = """
            using SharedDtos;
            using Verifiable.Contracts;

            namespace Consumer;

            [Verifiable]
            public static class Service
            {
                public static OrderState Convert(OrderDto dto)
                {
                    Spec.Ensures<OrderState>(s => s.Id == dto.Id && s.Total == dto.Total);
                    return dto.ToOrderState();
                }
            }
            """;

        var diags = await AnalyzerTestHarness.AnalyzeWithReferencesAsync(
            consumer,
            [libRef],
            null,
            null,
            new VerifiableGatekeeperAnalyzer(),
            new VerifiableSmtAnalyzer());
        Assert.Empty(diags.Where(d => d.Id == "AD0001" || d.Id == "VER104" || d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public async Task InvalidMapToDeclarations_ReportVER203WithElmFormatting()
    {
        const string missingProp = """
            using Verifiable.Contracts;
            namespace Sample;
            [MapTo(typeof(Target))]
            public class Source { public int Id { get; set; } }
            public readonly record struct Target(int Id, decimal Missing);
            """;

        var missingDiags = await AnalyzerTestHarness.AnalyzeAsync(missingProp, new VerifiableGatekeeperAnalyzer());
        Assert.Contains(missingDiags, d => d.Id == "VER203" && d.GetMessage().Contains("Missing"));

        const string narrowingTypeAlias = """
            using Verifiable.Contracts;
            namespace Sample;
            [TypeAlias<int>]
            public readonly partial record struct PositiveId { public static bool IsValid(int v) => v >= 1; }
            [MapTo(typeof(Target))]
            public class Source { public int Id { get; set; } }
            public readonly record struct Target(PositiveId Id);
            """;

        var narrowingDiags = await AnalyzerTestHarness.AnalyzeAsync(narrowingTypeAlias, new VerifiableGatekeeperAnalyzer());
        Assert.Contains(narrowingDiags, d => d.Id == "VER203" && d.GetMessage().Contains("[InboundPort]"));

        const string badMethodTarget = """
            using Verifiable.Contracts;
            namespace Sample;
            public class A { public int Id { get; set; } }
            public class B { public int Id { get; set; } }
            public class C { public int Id { get; set; } }
            public static partial class Mapper
            {
                [MapTo(typeof(C))]
                public static partial B Map(A a);
            }
            """;

        var mismatchDiags = await AnalyzerTestHarness.AnalyzeAsync(badMethodTarget, new VerifiableGatekeeperAnalyzer());
        Assert.Contains(mismatchDiags, d => d.Id == "VER203");
    }
}
