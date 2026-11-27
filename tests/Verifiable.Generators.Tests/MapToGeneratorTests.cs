using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Verifiable.Contracts;
using Xunit;

namespace Verifiable.Generators.Tests;

[MapTo(typeof(MappedPair))]
public sealed class PairInput
{
    public int Left { get; init; }
    public int Right;
}

public readonly record struct MappedPair(int Right, int Left);

public sealed class MapToGeneratorTests
{
    [Fact]
    public void ExtensionCopiesByNameNotDeclarationOrder()
    {
        var input = new PairInput { Left = 7, Right = -2 };
        Assert.Equal(new MappedPair(-2, 7), input.ToMappedPair());
    }

    private static readonly MetadataReference[] References =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(MapToAttribute).Assembly.Location).Distinct()
            .Select(path => MetadataReference.CreateFromFile(path)).ToArray();

    private static GeneratorDriverRunResult Run(string code, out Compilation output)
    {
        var compilation = CSharpCompilation.Create("MapToTest",
            [CSharpSyntaxTree.ParseText("#nullable enable\nusing Verifiable.Contracts;\n" + code)],
            References, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var driver = CSharpGeneratorDriver.Create(new MapToGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out output, out _);
        return driver.GetRunResult();
    }

    [Fact]
    public void EscapesNamesAndAvoidsHelperAndHintCollisions()
    {
        var result = Run("""
            namespace @event {
                [MapTo(typeof(Other.@class))] public record Source(int @int, string @event);
                [MapTo(typeof(Other.@class))] public record source(int @int, string @event);
                public class SourceMapToExtensions {}
                public static class Use {
                    public static Other.@class Convert(Source dto) => dto.Toclass();
                }
            }
            namespace Other { public record @class(string? @event, int @int); }
            """, out var output);
        Assert.Empty(result.Diagnostics);
        Assert.Empty(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.Equal(2, result.GeneratedTrees.Length);
        foreach (var tree in result.GeneratedTrees)
        {
            var text = tree.ToString();
            Assert.Contains("[global::Verifiable.Contracts.VerifiableAttribute]", text);
            Assert.DoesNotContain("PureAxiom", text);
            Assert.Contains("=> new global::Other.@class(source.@event, source.@int)", text);
        }
    }

    [Theory]
    [InlineData("public int Other;", "record T(int Value);", "Value")]
    [InlineData("public long Value;", "record T(int Value);", "Value")]
    [InlineData("public string? Value;", "record T(string Value);", "Value")]
    [InlineData("[System.Diagnostics.CodeAnalysis.MaybeNull] public string Value;", "record T(string Value);", "Value")]
    [InlineData("public int Value => 42;", "record T(int Value);", "Value")]
    [InlineData("public virtual int Value { get; set; }", "record T(int Value);", "Value")]
    [InlineData("public int Value { private get; set; }", "record T(int Value);", "Value")]
    [InlineData("public int Value;", "partial record T(int Value); partial record T { private int unseen; }", "positional")]
    [InlineData("public int Value;", "record struct T(int Value) { public int Extra { get; init; } }", "positional")]
    [InlineData("public int Value;", "record T(int Value) { public int Value => 42; }", "Value")]
    [InlineData("public int Value;", "record T(int Value) { static T() { throw new System.Exception(); } }", "construction")]
    [InlineData("public int Value;", "record T(int Value) { public T() : this(0) {} }", "construction")]
    [InlineData("public int Value;", "record Base(int X); record T(int Value) : Base(0);", "top-level")]
    [InlineData("public int Value; public int ToT() => 0;", "record T(int Value);", "conflicts")]
    public void InvalidMappingsReportAnErrorAndNeverEmitFallback(
        string sourceBody, string targetDeclaration, string expectedReason)
    {
        var result = Run($"[MapTo(typeof(T))] class Source {{ {sourceBody} }}\n{targetDeclaration}", out _);
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal("VER203", error.Id);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains(expectedReason, error.GetMessage());
        Assert.Empty(result.GeneratedTrees);
    }
}
