using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Verifiable.Contracts;
using Verifiable.Generators;

namespace Verifiable.Analyzers.Tests;

internal static class AnalyzerTestHarness
{
    public static Task<ImmutableArray<Diagnostic>> AnalyzeAsync(
        string source,
        params DiagnosticAnalyzer[] analyzers) =>
        AnalyzeWithOptionsAsync(source, null, analyzers);

    public static Task<ImmutableArray<Diagnostic>> AnalyzeWithOptionsAsync(
        string source,
        IReadOnlyDictionary<string, string>? buildProperties,
        params DiagnosticAnalyzer[] analyzers) =>
        AnalyzeWithReferencesAsync(source, buildProperties, [], analyzers);

    public static async Task<ImmutableArray<Diagnostic>> AnalyzeWithReferencesAsync(
        string source,
        IReadOnlyDictionary<string, string>? buildProperties,
        Type[] referenceTypes,
        params DiagnosticAnalyzer[] analyzers)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var syntaxTree = CSharpSyntaxTree.ParseText(source, parseOptions, path: "TestInput.cs");

        var refs = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => a.Location)
            .Concat(referenceTypes.Select(t => t.Assembly.Location))
            .Distinct(StringComparer.Ordinal)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToList<MetadataReference>();

        refs.Add(MetadataReference.CreateFromFile(typeof(VerifiableAttribute).Assembly.Location));
        refs.Add(MetadataReference.CreateFromFile(typeof(ImmutableDictionary).Assembly.Location));

        var compilation = CSharpCompilation.Create(
            "AnalyzerTestAssembly",
            [syntaxTree],
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new TypeAliasGenerator().AsSourceGenerator(), new InboundPortGenerator().AsSourceGenerator()],
            parseOptions: parseOptions);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var updatedCompilation, out _);

        AnalyzerOptions? analyzerOptions = buildProperties is null
            ? null
            : new AnalyzerOptions(
                ImmutableArray<AdditionalText>.Empty,
                new DictionaryConfigOptionsProvider(buildProperties));

        var withAnalyzers = updatedCompilation.WithAnalyzers(
            ImmutableArray.Create(analyzers),
            analyzerOptions);

        return await withAnalyzers.GetAllDiagnosticsAsync();
    }

    private sealed class DictionaryConfigOptionsProvider(IReadOnlyDictionary<string, string> props) : AnalyzerConfigOptionsProvider
    {
        private readonly DictionaryConfigOptions _global = new(props);
        public override AnalyzerConfigOptions GlobalOptions => _global;
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => DictionaryConfigOptions.Empty;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => DictionaryConfigOptions.Empty;
    }

    private sealed class DictionaryConfigOptions(IReadOnlyDictionary<string, string> props) : AnalyzerConfigOptions
    {
        public static readonly DictionaryConfigOptions Empty = new(new Dictionary<string, string>());
        public override bool TryGetValue(string key, [NotNullWhen(true)] out string? value) =>
            props.TryGetValue(key, out value);
    }
}
