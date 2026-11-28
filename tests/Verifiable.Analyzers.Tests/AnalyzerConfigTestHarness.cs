using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Verifiable.Analyzers.Tests;

/// <summary>
/// Runs analyzers the way csc does when a project has <c>.editorconfig</c> / <c>.globalconfig</c> files,
/// <c>&lt;NoWarn&gt;</c> or rulesets: severities from config files go to the compilation's
/// <see cref="SyntaxTreeOptionsProvider"/>, other keys to <see cref="AnalyzerConfigOptionsProvider"/>,
/// and command-line severities to <see cref="CompilationOptions.SpecificDiagnosticOptions"/>.
/// Suppressions are applied, so a test can also check that the setting really hides a diagnostic.
/// </summary>
internal static class AnalyzerConfigTestHarness
{
    /// <summary>The folder test sources and config files are placed in.</summary>
    public const string Root = "/src/";

    public static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(
        IEnumerable<(string Name, string Source)> sources,
        IEnumerable<(string Name, string Text)>? configFiles,
        IReadOnlyDictionary<string, ReportDiagnostic>? specificOptions,
        IReadOnlyDictionary<string, string>? buildProperties,
        params DiagnosticAnalyzer[] analyzers)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var trees = sources
            .Select(s => CSharpSyntaxTree.ParseText(s.Source, parseOptions, path: Root + s.Name))
            .ToArray();

        var configs = (configFiles ?? [])
            .Select(c => AnalyzerConfig.Parse(c.Text, Root + c.Name))
            .ToImmutableArray();
        var configSet = AnalyzerConfigSet.Create(configs, out _);
        var global = configSet.GlobalConfigOptions;
        var perTree = trees.ToDictionary(t => t, t => configSet.GetOptionsForSourcePath(t.FilePath));

        var globalKeys = global.AnalyzerOptions.ToBuilder();
        foreach (var property in buildProperties ?? new Dictionary<string, string>())
        {
            globalKeys["build_property." + property.Key] = property.Value;
        }

        var options = new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable,
                specificDiagnosticOptions: specificOptions)
            .WithSyntaxTreeOptionsProvider(new ConfigTreeOptions(perTree, global.TreeOptions));

        var compilation = CSharpCompilation.Create(
            "AnalyzerConfigTestAssembly",
            trees,
            DefaultReferences(),
            options);

        var analyzerOptions = new AnalyzerOptions(
            ImmutableArray<AdditionalText>.Empty,
            new ConfigOptionsProvider(
                globalKeys.ToImmutable(),
                perTree.ToDictionary(kv => kv.Key, kv => kv.Value.AnalyzerOptions)));

        return await compilation
            .WithAnalyzers(ImmutableArray.Create(analyzers), analyzerOptions)
            .GetAllDiagnosticsAsync();
    }

    private static List<MetadataReference> DefaultReferences()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var refs = new List<MetadataReference>();
        foreach (var location in AppDomain.CurrentDomain.GetAssemblies()
                     .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                     .Select(a => a.Location)
                     .Append(typeof(Verifiable.Contracts.VerifiableAttribute).Assembly.Location)
                     .Append(typeof(ImmutableArray).Assembly.Location))
        {
            if (seen.Add(location))
            {
                refs.Add(MetadataReference.CreateFromFile(location));
            }
        }

        return refs;
    }

    private sealed class ConfigTreeOptions(
        Dictionary<SyntaxTree, AnalyzerConfigOptionsResult> perTree,
        ImmutableDictionary<string, ReportDiagnostic> global) : SyntaxTreeOptionsProvider
    {
        public override GeneratedKind IsGenerated(SyntaxTree tree, CancellationToken cancellationToken) =>
            GeneratedKind.Unknown;

        public override bool TryGetDiagnosticValue(SyntaxTree tree, string diagnosticId, CancellationToken cancellationToken, out ReportDiagnostic severity)
        {
            severity = ReportDiagnostic.Default;
            return perTree.TryGetValue(tree, out var result) &&
                   TryGetIgnoringCase(result.TreeOptions, diagnosticId, out severity);
        }

        public override bool TryGetGlobalDiagnosticValue(string diagnosticId, CancellationToken cancellationToken, out ReportDiagnostic severity) =>
            TryGetIgnoringCase(global, diagnosticId, out severity);

        // csc looks diagnostic IDs up in config files without regard to case.
        private static bool TryGetIgnoringCase(ImmutableDictionary<string, ReportDiagnostic> map, string id, out ReportDiagnostic severity)
        {
            foreach (var entry in map)
            {
                if (string.Equals(entry.Key, id, StringComparison.OrdinalIgnoreCase))
                {
                    severity = entry.Value;
                    return true;
                }
            }

            severity = ReportDiagnostic.Default;
            return false;
        }
    }

    private sealed class ConfigOptionsProvider(
        ImmutableDictionary<string, string> global,
        Dictionary<SyntaxTree, ImmutableDictionary<string, string>> perTree) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = new Options(global);

        // csc gives each file the global keys too, overridden by the file's own sections.
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) =>
            new Options(perTree.TryGetValue(tree, out var own) ? global.SetItems(own) : global);

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => new Options(global);
    }

    private sealed class Options(ImmutableDictionary<string, string> values) : AnalyzerConfigOptions
    {
        private readonly ImmutableDictionary<string, string> _values = values.WithComparers(KeyComparer);

        public override bool TryGetValue(string key, [NotNullWhen(true)] out string? value) =>
            _values.TryGetValue(key, out value);

        public override IEnumerable<string> Keys => _values.Keys;
    }
}
