using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Verifiable.Contracts;
using Xunit;

namespace Verifiable.Generators.Tests;

public class RefinedTypeSnapshotTests
{
    private static (Compilation OutputCompilation, ImmutableArray<Diagnostic> GeneratorDiagnostics, IReadOnlyDictionary<string, string> Sources)
        RunGenerators(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));
        var refs = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .ToList<MetadataReference>();

        refs.Add(MetadataReference.CreateFromFile(typeof(VerifiableAttribute).Assembly.Location));
        refs.Add(MetadataReference.CreateFromFile(typeof(ImmutableDictionary).Assembly.Location));
        refs.Add(MetadataReference.CreateFromFile(typeof(JsonSerializer).Assembly.Location));

        var compilation = CSharpCompilation.Create(
            "GeneratorTestAssembly",
            [syntaxTree],
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new TypeAliasGenerator().AsSourceGenerator(), new RefinedTypeGenerator().AsSourceGenerator(), new InboundPortGenerator().AsSourceGenerator()],
            parseOptions: new CSharpParseOptions(LanguageVersion.Preview));

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var diagnostics);
        var result = driver.GetRunResult();
        var sources = result.Results
            .SelectMany(r => r.GeneratedSources)
            .ToDictionary(s => s.HintName, s => s.SourceText.ToString(), StringComparer.Ordinal);

        return (outputCompilation, diagnostics, sources);
    }

    [Fact]
    public void RefinedInt_GeneratesFullContractSurface_AndCompilesCleanly()
    {
        const string code = """
            using Verifiable.Contracts;

            namespace SampleDomain;

            [Refined<int>("Value >= 1")]
            public readonly partial record struct TodoId;
            """;

        var (output, genDiags, sources) = RunGenerators(code);

        Assert.Empty(genDiags);
        var compileErrors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.Empty(compileErrors);

        Assert.True(sources.TryGetValue("SampleDomain.TodoId.Refined.g.cs", out var generated));
        Assert.Contains("[global::Verifiable.Contracts.Verifiable]", generated);
        Assert.Contains("[global::Verifiable.Contracts.Invariant(\"Value >= 1\")]", generated);
        Assert.Contains("IComparable<TodoId>", generated);
        Assert.Contains("[global::Verifiable.Contracts.Requires(\"value >= 1\")]", generated);
        Assert.Contains("[global::Verifiable.Contracts.Ensures(\"result.Value == value\")]", generated);
        Assert.Contains("TodoIdJsonConverter", generated);
        Assert.Contains("ReadAsPropertyName", generated);
        Assert.Contains("WriteAsPropertyName", generated);
    }

    [Fact]
    public void RefinedZeroBasedBoundedInt_GeneratesPureAxiomReadUnchecked()
    {
        const string code = """
            using Verifiable.Contracts;

            namespace SampleDomain;

            [Refined<int>("Value >= 0 && Value < 64")]
            public readonly partial record struct BufferIndex;
            """;

        var (output, genDiags, sources) = RunGenerators(code);

        Assert.Empty(genDiags);
        Assert.Empty(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));

        var generated = sources["SampleDomain.BufferIndex.Refined.g.cs"];
        Assert.Contains("public const int UpperBoundExclusive = 64;", generated);
        Assert.Contains("[global::Verifiable.Contracts.PureAxiom]", generated);
        Assert.Contains("ReadUnchecked<T>(global::System.ReadOnlySpan<T> buffer)", generated);
    }

    [Fact]
    public void RefinedOnNonPartialClass_ReportsVER201WithFixLine()
    {
        const string code = """
            using Verifiable.Contracts;

            namespace SampleDomain;

            [Refined<int>("Value >= 1")]
            public class BadTodoId
            {
                public int Value { get; set; }
            }
            """;

        var (_, genDiags, _) = RunGenerators(code);

        var diag = Assert.Single(genDiags);
        Assert.Equal("VER201", diag.Id);
        Assert.Contains("Fix:", diag.GetMessage());
    }

    [Fact]
    public void RefinedWithInvalidPredicate_ReportsVER202WithFixLine()
    {
        const string code = """
            using Verifiable.Contracts;

            namespace SampleDomain;

            [Refined<int>("Value >=")]
            public readonly partial record struct BrokenId;
            """;

        var (_, genDiags, _) = RunGenerators(code);

        var diag = Assert.Single(genDiags);
        Assert.Equal("VER202", diag.Id);
        Assert.Contains("Fix:", diag.GetMessage());
    }

    [Fact]
    public void InboundPort_GeneratesRawDtosGateAndPort_AndCompilesCleanly()
    {
        const string code = """
            using System.Collections.Immutable;
            using Verifiable.Contracts;

            namespace SampleDomain;

            [Refined<int>("Value >= 1")]
            public readonly partial record struct ItemId;

            [Refined<string>("Length is >= 1 and <= 50")]
            public readonly partial record struct ItemTitle;

            [Verifiable]
            [Invariant("Items.Count == NextId.Value - 1")]
            public readonly record struct Board(
                ItemId NextId,
                ImmutableDictionary<ItemId, ItemTitle> Items);

            public static class Engine
            {
                [Verifiable, InboundPort]
                [Requires("board.NextId.Value >= 1")]
                public static Board Add(Board board, ItemTitle title)
                {
                    var next = ItemId.From(board.NextId.Value + 1);
                    return board with { NextId = next, Items = board.Items.Add(board.NextId, title) };
                }
            }
            """;

        var (output, genDiags, sources) = RunGenerators(code);

        Assert.Empty(genDiags);
        var compileErrors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.Empty(compileErrors);

        Assert.True(sources.ContainsKey("SampleDomain.Board.Gate.g.cs"));
        Assert.True(sources.ContainsKey("SampleDomain.EnginePort.InboundPort.g.cs"));
    }
}
