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
        AnalyzeWithOptionsAndGeneratorsAsync(source, buildProperties, null, analyzers);

    public static Task<ImmutableArray<Diagnostic>> AnalyzeWithOptionsAndGeneratorsAsync(
        string source,
        IReadOnlyDictionary<string, string>? buildProperties,
        IEnumerable<ISourceGenerator>? extraGenerators,
        params DiagnosticAnalyzer[] analyzers) =>
        AnalyzeWithReferencesAsync(source, null, buildProperties, extraGenerators, analyzers);

    public static MetadataReference CompileLibraryReference(
        string source,
        string assemblyName = "ReferencedLibrary",
        IEnumerable<MetadataReference>? extraReferences = null)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var syntaxTree = CSharpSyntaxTree.ParseText(source, parseOptions, path: $"{assemblyName}.cs");
        var refs = CollectDefaultReferences(extraReferences);

        var compilation = CSharpCompilation.Create(
            assemblyName,
            [syntaxTree],
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: CreateDefaultGenerators(null),
            parseOptions: parseOptions);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var updatedCompilation, out var genDiagnostics);

        var genErrors = genDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (genErrors.Count > 0)
        {
            throw new InvalidOperationException($"Library generator errors in {assemblyName}: " +
                string.Join(Environment.NewLine, genErrors.Select(d => d.ToString())));
        }

        using var ms = new System.IO.MemoryStream();
        var emitResult = updatedCompilation.Emit(ms);
        if (!emitResult.Success)
        {
            throw new InvalidOperationException($"Failed to emit library {assemblyName}: " +
                string.Join(Environment.NewLine, emitResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        }

        ms.Position = 0;
        return MetadataReference.CreateFromImage(ms.ToArray());
    }

    public static async Task<ImmutableArray<Diagnostic>> AnalyzeWithReferencesAsync(
        string source,
        IEnumerable<MetadataReference>? extraReferences,
        IReadOnlyDictionary<string, string>? buildProperties,
        IEnumerable<ISourceGenerator>? extraGenerators,
        params DiagnosticAnalyzer[] analyzers)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var syntaxTree = CSharpSyntaxTree.ParseText(source, parseOptions, path: "TestInput.cs");
        var refs = CollectDefaultReferences(extraReferences);

        var compilation = CSharpCompilation.Create(
            "AnalyzerTestAssembly",
            [syntaxTree],
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: CreateDefaultGenerators(extraGenerators),
            parseOptions: parseOptions);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var updatedCompilation, out var genDiagnostics);

        AnalyzerOptions? analyzerOptions = buildProperties is null
            ? null
            : new AnalyzerOptions(
                ImmutableArray<AdditionalText>.Empty,
                new DictionaryConfigOptionsProvider(buildProperties));

        var withAnalyzers = updatedCompilation.WithAnalyzers(
            ImmutableArray.Create(analyzers),
            analyzerOptions);

        var analyzerDiagnostics = await withAnalyzers.GetAllDiagnosticsAsync();
        return genDiagnostics.IsDefaultOrEmpty
            ? analyzerDiagnostics
            : analyzerDiagnostics.AddRange(genDiagnostics);
    }

    public static async Task<ImmutableArray<Diagnostic>> AnalyzeIncludingSuppressedAsync(
        string source,
        params DiagnosticAnalyzer[] analyzers)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var syntaxTree = CSharpSyntaxTree.ParseText(source, parseOptions, path: "TestInput.cs");
        var refs = CollectDefaultReferences(null);

        var compilation = CSharpCompilation.Create(
            "AnalyzerTestAssembly",
            [syntaxTree],
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        var options = new CompilationWithAnalyzersOptions(
            options: null,
            onAnalyzerException: null,
            concurrentAnalysis: true,
            logAnalyzerExecutionTime: false,
            reportSuppressedDiagnostics: true);

        var withAnalyzers = compilation.WithAnalyzers(ImmutableArray.Create(analyzers), options);
        return await withAnalyzers.GetAllDiagnosticsAsync();
    }

    private static List<MetadataReference> CollectDefaultReferences(IEnumerable<MetadataReference>? extraReferences)
    {
        var refLocations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var refs = new List<MetadataReference>();
        void AddRef(string loc)
        {
            if (!string.IsNullOrEmpty(loc) && refLocations.Add(loc))
            {
                refs.Add(MetadataReference.CreateFromFile(loc));
            }
        }

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!asm.IsDynamic && !string.IsNullOrEmpty(asm.Location))
            {
                AddRef(asm.Location);
            }
        }

        AddRef(typeof(VerifiableAttribute).Assembly.Location);
        AddRef(typeof(ImmutableDictionary).Assembly.Location);
        AddRef(typeof(System.Text.RegularExpressions.Regex).Assembly.Location);

        if (extraReferences is not null)
        {
            refs.AddRange(extraReferences);
        }

        return refs;
    }

    private static List<ISourceGenerator> CreateDefaultGenerators(IEnumerable<ISourceGenerator>? extraGenerators)
    {
        var generators = new List<ISourceGenerator>
        {
            new TypeAliasGenerator().AsSourceGenerator(),
            new InboundPortGenerator().AsSourceGenerator(),
            new MapToGenerator().AsSourceGenerator(),
            new ContractMetadataGenerator().AsSourceGenerator()
        };
        if (extraGenerators is not null)
        {
            generators.AddRange(extraGenerators);
        }

        return generators;
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
