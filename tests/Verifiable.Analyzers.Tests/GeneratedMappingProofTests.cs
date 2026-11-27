using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Verifiable.Analyzers.Smt;
using Verifiable.Generators;
using Xunit;

namespace Verifiable.Analyzers.Tests;

public class GeneratedMappingProofTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FieldByFieldPostconditionTracksGeneratedImplementation(bool swapFields)
    {
        var input = AnalyzerTestHarness.CreateCompilation("""
            using Verifiable.Contracts;
            namespace Example;

            [MapTo(typeof(Target))]
            public sealed class Source
            {
                public int Left { get; set; }
                public int Right { get; set; }
            }
            public sealed record Target(int Right, int Left);

            [Verifiable] public static class Adapter
            {
                public static Target Convert(Source source)
                {
                    Spec.Ensures<Target>(t => t.Left == source.Left && t.Right == source.Right);
                    return source.ToTarget();
                }
            }
            """);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new MapToGenerator().AsSourceGenerator()], parseOptions: AnalyzerTestHarness.ParseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(input, out var compilation, out var generationDiagnostics);
        Assert.Empty(generationDiagnostics);
        var emitted = Assert.Single(driver.GetRunResult().GeneratedTrees);
        if (swapFields)
        {
            // Change only the generated constructor call; keep names, types and the caller's contract intact.
            var root = await emitted.GetRootAsync();
            var creation = Assert.Single(root.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>());
            var args = creation.ArgumentList!.Arguments;
            Assert.Equal(2, args.Count);
            var swapped = root.ReplaceNodes(args, (arg, _) =>
                arg.WithExpression(arg == args[0] ? args[1].Expression : args[0].Expression));
            compilation = compilation.ReplaceSyntaxTree(emitted, emitted.WithRootAndOptions(swapped, emitted.Options));
        }

        var diagnostics = await compilation.WithAnalyzers([new VerifiableGatekeeperAnalyzer(), new VerifiableSmtAnalyzer()])
            .GetAllDiagnosticsAsync();
        var failures = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error ||
            d.Id.StartsWith("VER", StringComparison.Ordinal) || d.Id == "AD0001");
        if (swapFields)
        {
            var failure = Assert.Single(failures);
            Assert.Equal("VER102", failure.Id);
            Assert.Same(input.SyntaxTrees.Single(), failure.Location.SourceTree);
        }
        else
            Assert.Empty(failures);
    }
}
