using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Verifiable.Analyzers.Smt;
using Xunit;

namespace Verifiable.Analyzers.Tests;

public class InitializerProjectionTests
{
    [Theory]
    [InlineData("s == d")]
    [InlineData("s.Equals(d)")]
    public async Task FreshDtoDoesNotAcquireSourceReferenceIdentity(string comparison)
    {
        await Check($$"""
            using Verifiable.Contracts;
            public class Dto { public int Id { get; set; } }
            [Verifiable] public static class Mapper
            {
                public static Dto Run(Dto d)
                {
                    Spec.Ensures<Dto>(s => {{comparison}});
                    return new Dto { Id = d.Id };
                }
            }
            """, "VER101", allowUnverifiedCall: true);
    }

    [Theory]
    [InlineData("new Box(new D { X = 1 }) == new Box(new D { X = 1 })")]
    [InlineData("new Box(new D { X = 1 }).Equals(new Box(new D { X = 1 }))")]
    [InlineData("new Outer(new Box(new D { X = 1 })) == new Outer(new Box(new D { X = 1 }))")]
    public async Task SynthesizedRecordEqualityDoesNotEraseNestedIdentity(string comparison)
    {
        await Check($$"""
            using Verifiable.Contracts;
            public sealed class D { public int X { get; init; } }
            public readonly record struct Box(D Value);
            public sealed record Outer(Box Inner);
            [Verifiable] public static class Mapper
            {
                public static bool Compare()
                {
                    Spec.Ensures<bool>(r => r);
                    return {{comparison}};
                }
            }
            """, "VER101", allowUnverifiedCall: true);
    }

    [Theory]
    [InlineData("readonly record struct State", "d.Id", null)]
    [InlineData("record State", "d.Other", "VER102")]
    [InlineData("sealed class State", "d.Id", null)]
    public async Task InitOnlyHelperMapsIndependentFields(string declaration, string id, string? failure)
    {
        await Check($$"""
            using Verifiable.Contracts;
            public class Dto { public int Id { get; set; } public int Other { get; set; } }
            public {{declaration}} { public int Id { get; init; } public int Other { get; init; } }
            [Verifiable] public static class Mapper
            {
                static State Project(Dto d) => new() { Other = d.Other, Id = {{id}} };
                public static State Run(Dto d)
                {
                    Spec.Ensures<State>(s => s.Id == d.Id && s.Other == Project(d).Other);
                    return Project(d);
                }
            }
            """, failure);
    }

    [Theory]
    [InlineData("d.Total", null)]
    [InlineData("0m", "VER102")]
    public async Task ImmediatelyReturnedMutableAutoPropertiesCarryValues(string total, string? failure)
    {
        await Check($$"""
            using Verifiable.Contracts;
            public class Dto { public int Id { get; set; } public decimal Total { get; set; } }
            public class Response { public int Id { get; set; } public decimal Total { get; set; } }
            [Verifiable] public static class Mapper
            {
                public static Response Run(Dto d)
                {
                    Spec.Ensures<Response>(s => s.Id == d.Id && s.Total == d.Total);
                    return new Response { Total = {{total}}, Id = d.Id };
                }
            }
            """, failure);
    }

    [Theory]
    [InlineData("public State() { }", "return new State { X = x };")]
    [InlineData("public int Y { get; init; } = 77;", "return new State { X = x };")]
    [InlineData("public int Y { get => 5; set { } }", "return new State { X = x, Y = x };")]
    [InlineData("", "var s = new State { X = x }; var alias = s; return alias;")]
    [InlineData("", "var s = new State { X = x }; s.X++; return s;")]
    public async Task UnsafeConstructionsAreNotInterpretedAsStoredValues(string member, string body)
    {
        var source = $$"""
            using Verifiable.Contracts;
            public class State { public int X { get; set; } {{member}} }
            [Verifiable] public static class Mapper
            {
                public static State Run(int x)
                {
                    Spec.Ensures<State>(s => s.X == x);
                    {{body}}
                }
            }
            """;
        var diagnostics = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableSmtAnalyzer(), new VerifiableGatekeeperAnalyzer());
        Assert.DoesNotContain(diagnostics, d => d.Id is "AD0001" or "VER103" or "VER104" || d.Id.StartsWith("CS") && d.Severity == DiagnosticSeverity.Error);
        Assert.Contains(diagnostics, d => d.Id == "VER001");
        Assert.Contains(diagnostics, d => d.Id == "VER101");
    }

    private static async Task Check(string source, string? failure, bool allowUnverifiedCall = false)
    {
        var diagnostics = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableSmtAnalyzer(), new VerifiableGatekeeperAnalyzer());
        Assert.Empty(diagnostics.Where(d => (d.Severity == DiagnosticSeverity.Error || d.Id is "VER104" or "AD0001") &&
            d.Id != failure && !(allowUnverifiedCall && d.Id == "VER006")));
        if (failure is not null) Assert.Contains(diagnostics, d => d.Id == failure);
    }
}
