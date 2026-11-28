using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Verifiable.Contracts;
using Xunit;

namespace Verifiable.Generators.Tests;

/// <summary>
/// The <c>[InboundPort]</c> runtime gate and the cross-assembly contract export use the same rule as the prover:
/// only a <c>Spec.Requires</c> / <c>Spec.RequiresWhen</c> in the leading contract block is a precondition.
/// </summary>
public class RequiresPlacementGeneratorTests
{
    private const string Source = """
        using Verifiable.Contracts;

        namespace Shop;

        [Verifiable]
        public static class Orders
        {
            [InboundPort]
            public static int Place(int quantity, int stock)
            {
                Spec.Ensures<int>(r => r >= 0, "Remaining stock is never negative.");
                Spec.Requires(quantity >= 1, "LEADING_QTY");
                Spec.RequiresWhen(stock > 0, quantity <= stock, "LEADING_WHEN");
                var remaining = stock - quantity;
                Spec.Requires(remaining < 1000, "AFTER_STATEMENT");
                if (remaining > 10)
                {
                    Spec.Requires(remaining < 500, "NESTED_IF");
                }
                return remaining < 0 ? 0 : remaining;
            }
        }
        """;

    private static ImmutableArray<GeneratedSourceResult> Run(params IIncrementalGenerator[] generators)
    {
        var tree = CSharpSyntaxTree.ParseText(Source, new CSharpParseOptions(LanguageVersion.Preview));
        var refs = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .ToList<MetadataReference>();
        refs.Add(MetadataReference.CreateFromFile(typeof(VerifiableAttribute).Assembly.Location));
        refs.Add(MetadataReference.CreateFromFile(typeof(ImmutableArray).Assembly.Location));

        var compilation = CSharpCompilation.Create(
            "PlacementGeneratorTest",
            [tree],
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: generators.Select(g => g.AsSourceGenerator()),
            parseOptions: new CSharpParseOptions(LanguageVersion.Preview));
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(output.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
        return driver.GetRunResult().Results.SelectMany(r => r.GeneratedSources).ToImmutableArray();
    }

    [Fact]
    public void InboundPortGate_ChecksOnlyLeadingRequires()
    {
        var gate = string.Concat(Run(new InboundPortGenerator()).Select(s => s.SourceText.ToString()));

        Assert.Contains("LEADING_QTY", gate);
        Assert.Contains("LEADING_WHEN", gate);
        Assert.DoesNotContain("AFTER_STATEMENT", gate);
        Assert.DoesNotContain("NESTED_IF", gate);
    }

    [Fact]
    public void ContractMetadata_ExportsOnlyLeadingRequires()
    {
        var metadata = string.Concat(Run(new ContractMetadataGenerator()).Select(s => s.SourceText.ToString()));

        Assert.Contains("LEADING_QTY", metadata);
        Assert.Contains("LEADING_WHEN", metadata);
        Assert.Contains("Remaining stock is never negative.", metadata);
        Assert.DoesNotContain("AFTER_STATEMENT", metadata);
        Assert.DoesNotContain("NESTED_IF", metadata);
    }
}
