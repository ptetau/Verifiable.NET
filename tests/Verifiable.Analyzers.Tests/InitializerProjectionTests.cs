using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Verifiable.Analyzers.Smt;
using Xunit;

namespace Verifiable.Analyzers.Tests;

public class InitializerProjectionTests
{
    [Fact]
    public async Task ObjectInitializerHelperProjection_ProvesFieldsAndDetectsSwap()
    {
        const string validSource = """
            using Verifiable.Contracts;

            namespace Sample;

            public record Dto(int Left, int Right);
            public readonly record struct State
            {
                public int A { get; init; }
                public int B { get; init; }
            }

            [Verifiable]
            public static class Mapper
            {
                static State Project(Dto d) => new State { A = d.Left, B = d.Right };

                public static State Adapt(Dto d)
                {
                    Spec.Ensures<State>(s => s.A == d.Left && s.B == d.Right);
                    return Project(d);
                }
            }
            """;

        var okDiags = await AnalyzerTestHarness.AnalyzeAsync(validSource, new VerifiableGatekeeperAnalyzer(), new VerifiableSmtAnalyzer());
        Assert.Empty(okDiags.Where(d => d.Id == "AD0001" || d.Id == "VER104" || d.Severity == DiagnosticSeverity.Error));

        var swapped = validSource.Replace("A = d.Left, B = d.Right", "A = d.Right, B = d.Left");
        var badDiags = await AnalyzerTestHarness.AnalyzeAsync(swapped, new VerifiableGatekeeperAnalyzer(), new VerifiableSmtAnalyzer());
        Assert.Contains(badDiags, d => d.Id == "VER102");
    }

    [Fact]
    public async Task D16_WriteOnceBoundaryMutableDtoConstruction_VerifiesCleanly()
    {
        const string source = """
            using Verifiable.Contracts;

            namespace Sample;

            public class OrderDto { public int Id { get; set; } public decimal Total { get; set; } }
            public class OrderResponse { public int Id { get; set; } public decimal Total { get; set; } }

            [Verifiable]
            public static class OrderMapper
            {
                public static OrderResponse ToResponse(OrderDto dto)
                {
                    Spec.Ensures<OrderResponse>(r => r.Id == dto.Id && r.Total == dto.Total);
                    return new OrderResponse { Id = dto.Id, Total = dto.Total };
                }
            }
            """;

        var diags = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableGatekeeperAnalyzer(), new VerifiableSmtAnalyzer());
        Assert.Empty(diags.Where(d => d.Id == "AD0001" || d.Id == "VER104" || d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public async Task PostInitMutationOfBoundaryDto_StillFailsVER001()
    {
        const string source = """
            using Verifiable.Contracts;

            namespace Sample;

            public class OrderResponse { public int Id { get; set; } }

            [Verifiable]
            public static class BadMapper
            {
                public static OrderResponse MutateAfterInit(int id)
                {
                    var resp = new OrderResponse();
                    resp.Id = id;
                    return resp;
                }
            }
            """;

        var diags = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableGatekeeperAnalyzer());
        Assert.Contains(diags, d => d.Id == "VER001");
    }

    [Fact]
    public async Task ReferenceEqualityOnMutableDtoClass_IsRejectedWithVER101()
    {
        const string source = """
            using Verifiable.Contracts;

            namespace Sample;

            public class OrderResponse { public int Id { get; set; } }

            [Verifiable]
            public static class IdentityCheck
            {
                public static bool SameRef()
                {
                    var a = new OrderResponse { Id = 1 };
                    var b = new OrderResponse { Id = 1 };
                    Spec.Ensures<bool>(r => r == (a == b));
                    return a == b;
                }
            }
            """;

        var diags = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableGatekeeperAnalyzer(), new VerifiableSmtAnalyzer());
        Assert.DoesNotContain(diags, d => d.Id == "AD0001" || d.Id == "VER104");
        Assert.Contains(diags, d => d.Id == "VER101");
    }
}
