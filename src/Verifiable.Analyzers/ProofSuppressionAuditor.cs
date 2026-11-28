using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Verifiable.Core;

namespace Verifiable.Analyzers;

/// <summary>
/// Reports <c>VER107</c> when a project or file weakens a proof diagnostic (<c>VER101</c>-<c>VER106</c>):
/// <c>&lt;NoWarn&gt;</c>, a ruleset, an <c>.editorconfig</c> or <c>.globalconfig</c> severity, a
/// <c>#pragma warning disable</c> or a <c>[SuppressMessage]</c>. Without this, unproved code builds
/// as if it were proved and nobody is told.
/// </summary>
/// <remarks>
/// Only projects that contain <c>[Verifiable]</c> code are audited, and file-level weakenings only in
/// files that declare <c>[Verifiable]</c> code, because nothing is proved anywhere else. Which IDs are
/// guarded and which severities count as weakened is decided in <see cref="SuppressionKernel"/>.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ProofSuppressionAuditor : DiagnosticAnalyzer
{
    internal static readonly DiagnosticDescriptor ProofCheckWeakened = new(
        id: SuppressionKernel.AuditId,
        title: "A proof check was weakened or switched off",
        messageFormat: "{0}",
        category: "Verifiable.Audit",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    /// <summary>Every ID the kernel guards, in report order. A test checks this matches the kernel.</summary>
    internal static readonly ImmutableArray<string> GuardedIds =
        ["VER101", "VER102", "VER103", "VER104", "VER105", "VER106", "VER107"];

    /// <summary>The diagnostic category of the proof checks VER101-VER106.</summary>
    internal const string ProofCategory = "Verifiable.SMT";

    private const string BulkKey = "dotnet_analyzer_diagnostic.severity";

    private const string Spirit =
        "Proof diagnostics are errors so that code that was not proved cannot build. Lowering one hides " +
        "the result without changing it: the code still has not been proved.";

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [ProofCheckWeakened];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(start =>
        {
            var verifiableTrees = new ConcurrentDictionary<SyntaxTree, bool>();
            var pragmas = new ConcurrentBag<PragmaFinding>();
            var attributes = new ConcurrentBag<AttributeFinding>();

            start.RegisterSymbolAction(
                symbolCtx => CollectSymbol(symbolCtx.Symbol, verifiableTrees, attributes, symbolCtx.CancellationToken),
                SymbolKind.NamedType, SymbolKind.Method, SymbolKind.Property, SymbolKind.Field, SymbolKind.Event);

            start.RegisterSyntaxTreeAction(treeCtx => CollectPragmas(treeCtx.Tree, pragmas, treeCtx.CancellationToken));

            start.RegisterCompilationEndAction(endCtx =>
            {
                if (verifiableTrees.IsEmpty)
                {
                    return;
                }

                var audit = new Audit(endCtx, verifiableTrees.Keys.ToImmutableHashSet(), pragmas.ToList(), attributes.ToList());
                audit.Run();
            });
        });
    }

    private static void CollectSymbol(
        ISymbol symbol,
        ConcurrentDictionary<SyntaxTree, bool> verifiableTrees,
        ConcurrentBag<AttributeFinding> attributes,
        System.Threading.CancellationToken ct)
    {
        if (VerifiableSymbolHelpers.GetScope(symbol) == VerificationScope.Verifiable)
        {
            foreach (var syntaxRef in symbol.DeclaringSyntaxReferences)
            {
                verifiableTrees.TryAdd(syntaxRef.SyntaxTree, true);
            }
        }

        foreach (var attribute in symbol.GetAttributes())
        {
            if (TryGetSuppressedId(attribute, out var checkId) &&
                attribute.ApplicationSyntaxReference is { } appRef &&
                ContainsVerifiableCode(symbol))
            {
                attributes.Add(new AttributeFinding(checkId, appRef.GetSyntax(ct).GetLocation(), symbol.ToDisplayString()));
            }
        }
    }

    private static void CollectPragmas(SyntaxTree tree, ConcurrentBag<PragmaFinding> pragmas, System.Threading.CancellationToken ct)
    {
        var root = tree.GetRoot(ct);
        if (!root.ContainsDirectives)
        {
            return;
        }

        foreach (var trivia in root.DescendantTrivia(descendIntoTrivia: true))
        {
            if (trivia.GetStructure() is not PragmaWarningDirectiveTriviaSyntax pragma ||
                !pragma.DisableOrRestoreKeyword.IsKind(SyntaxKind.DisableKeyword))
            {
                continue;
            }

            var codes = pragma.ErrorCodes
                .Select(c => c is IdentifierNameSyntax name ? name.Identifier.ValueText : c.ToString().Trim())
                .ToImmutableArray();
            var text = codes.IsEmpty ? "#pragma warning disable" : "#pragma warning disable " + string.Join(", ", codes);
            pragmas.Add(new PragmaFinding(tree, pragma.GetLocation(), codes, text));
        }
    }

    /// <summary>Whether a <c>[SuppressMessage]</c> on this symbol could hide a proof diagnostic.</summary>
    private static bool ContainsVerifiableCode(ISymbol symbol)
    {
        if (VerifiableSymbolHelpers.GetScope(symbol) == VerificationScope.Verifiable)
        {
            return true;
        }

        return symbol is INamedTypeSymbol type && type.GetMembers().Any(ContainsVerifiableCode);
    }

    private static bool TryGetSuppressedId(AttributeData attribute, out string checkId)
    {
        checkId = string.Empty;
        var name = attribute.AttributeClass?.ToDisplayString();
        if (name is not ("System.Diagnostics.CodeAnalysis.SuppressMessageAttribute" or
                         "System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessageAttribute") ||
            attribute.ConstructorArguments.Length < 2 ||
            attribute.ConstructorArguments[1].Value is not string id ||
            !SuppressionKernel.SuppressMessageTargetsGuardedId(id))
        {
            return false;
        }

        checkId = id;
        return true;
    }

    private static ConfiguredSeverity ToConfigured(ReportDiagnostic report) => report switch
    {
        ReportDiagnostic.Error => ConfiguredSeverity.Error,
        ReportDiagnostic.Warn => ConfiguredSeverity.Warning,
        ReportDiagnostic.Info => ConfiguredSeverity.Info,
        ReportDiagnostic.Hidden => ConfiguredSeverity.Hidden,
        ReportDiagnostic.Suppress => ConfiguredSeverity.Suppressed,
        _ => ConfiguredSeverity.Default
    };

    private static string SeverityWord(ConfiguredSeverity severity) => severity switch
    {
        ConfiguredSeverity.Warning => "warning",
        ConfiguredSeverity.Info => "suggestion",
        ConfiguredSeverity.Hidden => "silent",
        ConfiguredSeverity.Suppressed => "none",
        ConfiguredSeverity.Error => "error",
        _ => "default"
    };

    internal static string Describe(string id) => id switch
    {
        "VER101" => "VER101 (unsupported contract)",
        "VER102" => "VER102 (proof failed)",
        "VER103" => "VER103 (solver could not decide)",
        "VER104" => "VER104 (Z3 missing)",
        "VER105" => "VER105 (contradictory premises)",
        "VER106" => "VER106 (vacuity check undecided)",
        "VER107" => "VER107 (this audit)",
        _ => id
    };

    private static string DescribeAll(IEnumerable<string> ids) => string.Join(", ", ids.Select(Describe));

    private static string FileName(SyntaxTree tree) =>
        string.IsNullOrEmpty(tree.FilePath) ? "<unnamed>" : System.IO.Path.GetFileName(tree.FilePath);

    private static string Where(Location location)
    {
        var span = location.GetLineSpan();
        return $"{System.IO.Path.GetFileName(span.Path)}:{span.StartLinePosition.Line + 1}";
    }

    private sealed record PragmaFinding(SyntaxTree Tree, Location Location, ImmutableArray<string> Codes, string Text)
    {
        public bool IsBare => Codes.IsEmpty;
    }

    private sealed record AttributeFinding(string CheckId, Location Location, string Target);

    /// <summary>One VER107 report.</summary>
    private sealed record Finding(
        Location ReportAt,
        Location ShowAt,
        string SortKey,
        string Summary,
        string Intro,
        string Fix);

    private sealed class Audit
    {
        private readonly CompilationAnalysisContext _ctx;
        private readonly ImmutableHashSet<SyntaxTree> _verifiableTrees;
        private readonly List<PragmaFinding> _pragmas;
        private readonly List<AttributeFinding> _attributes;
        private readonly bool _allowMissingSolver;
        private readonly SyntaxTreeOptionsProvider? _treeOptions;
        private readonly List<Finding> _findings = new();

        public Audit(
            CompilationAnalysisContext ctx,
            ImmutableHashSet<SyntaxTree> verifiableTrees,
            List<PragmaFinding> pragmas,
            List<AttributeFinding> attributes)
        {
            _ctx = ctx;
            _verifiableTrees = verifiableTrees;
            _pragmas = pragmas;
            _attributes = attributes;
            _treeOptions = ctx.Compilation.Options.SyntaxTreeOptionsProvider;
            ctx.Options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue(
                "build_property.VerifiableAllowMissingZ3", out var allow);
            _allowMissingSolver = allow == "1" || string.Equals(allow, "true", StringComparison.OrdinalIgnoreCase);
        }

        private bool Weakens(string id, ConfiguredSeverity severity) =>
            SuppressionKernel.IsWeakening(id, severity, _allowMissingSolver);

        public void Run()
        {
            AuditCompilerOptions();
            AuditGlobalConfig();
            AuditTreeConfig();
            AuditCategoryAndBulkSettings();
            AuditPragmas();
            AuditAttributes();

            foreach (var finding in _findings.OrderBy(f => f.SortKey, StringComparer.Ordinal))
            {
                var message = ElmDiagnosticFormatter.Format(
                    "PROOF CHECK WEAKENED",
                    finding.ShowAt,
                    finding.Intro,
                    spirit: Spirit,
                    fixHint: finding.Fix,
                    summary: finding.Summary);
                _ctx.ReportDiagnostic(Diagnostic.Create(ProofCheckWeakened, finding.ReportAt, message));
            }
        }

        private void Add(Location showAt, Location reportAt, string sortKey, string summary, string intro, string fix) =>
            _findings.Add(new Finding(reportAt, showAt, sortKey, summary, intro, fix));

        private void AddProjectWide(string sortKey, string summary, string intro, string fix) =>
            Add(Location.None, Location.None, "0|" + sortKey, summary, intro, fix);

        /// <summary><c>&lt;NoWarn&gt;</c>, <c>-nowarn</c> and rulesets end up in the compilation options.</summary>
        private void AuditCompilerOptions()
        {
            foreach (var entry in _ctx.Compilation.Options.SpecificDiagnosticOptions.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                var severity = ToConfigured(entry.Value);
                if (!Weakens(entry.Key, severity))
                {
                    continue;
                }

                var id = entry.Key;
                if (severity == ConfiguredSeverity.Suppressed)
                {
                    AddProjectWide(
                        "a|" + id,
                        $"Proof check weakened: {id} is switched off for the whole project by <NoWarn>, -nowarn or a ruleset. Remove {id} from <NoWarn> (or the ruleset).",
                        $"The compiler options switch off {Describe(id)} for every file in this project, through `<NoWarn>`, `-nowarn:{id}` or a ruleset. Code with this problem now builds as if it were proved.",
                        $"Remove `{id}` from `<NoWarn>` in the project file or `Directory.Build.props`, from `-nowarn:`, or from the ruleset.");
                }
                else
                {
                    AddProjectWide(
                        "a|" + id,
                        $"Proof check weakened: {id} is lowered to {SeverityWord(severity)} for the whole project by a ruleset. Set it back to Error.",
                        $"The compiler options (usually a ruleset) lower {Describe(id)} to `{SeverityWord(severity)}` for every file in this project. Code with this problem now builds as if it were proved.",
                        $"Remove `{id}` from the ruleset (`<CodeAnalysisRuleSet>`) or set its action to `Error`.");
                }
            }
        }

        /// <summary><c>dotnet_diagnostic.ID.severity</c> in a global analyzer config (<c>.globalconfig</c>).</summary>
        private void AuditGlobalConfig()
        {
            if (_treeOptions is null)
            {
                return;
            }

            foreach (var id in GuardedIds)
            {
                if (!_treeOptions.TryGetGlobalDiagnosticValue(id, _ctx.CancellationToken, out var report))
                {
                    continue;
                }

                var severity = ToConfigured(report);
                if (!Weakens(id, severity))
                {
                    continue;
                }

                var setting = $"dotnet_diagnostic.{id}.severity = {SeverityWord(severity)}";
                AddProjectWide(
                    "b|" + id,
                    $"Proof check weakened: `{setting}` in a global analyzer config lowers {id} for the whole project. Remove it or set it to error.",
                    $"A global analyzer config (`.globalconfig`, or a file with `is_global = true`) sets `{setting}`. That lowers {Describe(id)} for every file, so code with this problem builds as if it were proved.",
                    $"Remove `{setting}` from the global analyzer config, or set it to `error`.");
            }
        }

        /// <summary><c>dotnet_diagnostic.ID.severity</c> in an <c>.editorconfig</c> section that covers verifiable files.</summary>
        private void AuditTreeConfig()
        {
            if (_treeOptions is null)
            {
                return;
            }

            var groups = new SortedDictionary<string, (string Id, ConfiguredSeverity Severity, List<string> Files)>(StringComparer.Ordinal);
            foreach (var tree in _verifiableTrees)
            {
                foreach (var id in GuardedIds)
                {
                    if (!_treeOptions.TryGetDiagnosticValue(tree, id, _ctx.CancellationToken, out var report))
                    {
                        continue;
                    }

                    var severity = ToConfigured(report);
                    if (!Weakens(id, severity))
                    {
                        continue;
                    }

                    var key = id + "|" + (int)severity;
                    if (!groups.TryGetValue(key, out var group))
                    {
                        group = (id, severity, new List<string>());
                        groups[key] = group;
                    }

                    group.Files.Add(FileName(tree));
                }
            }

            foreach (var group in groups.Values)
            {
                var setting = $"dotnet_diagnostic.{group.Id}.severity = {SeverityWord(group.Severity)}";
                var files = FormatFiles(group.Files);
                AddProjectWide(
                    "c|" + group.Id,
                    $"Proof check weakened: `{setting}` in .editorconfig lowers {group.Id} in {files}. Remove it or set it to error.",
                    $"An `.editorconfig` section sets `{setting}` for {files}. That lowers {Describe(group.Id)} there, so code with this problem builds as if it were proved.",
                    $"Remove `{setting}` from the `.editorconfig` section that matches these files, or set it to `error`.");
            }
        }

        /// <summary>
        /// <c>dotnet_analyzer_diagnostic.category-Verifiable.SMT.severity</c> and <c>dotnet_analyzer_diagnostic.severity</c>.
        /// An ID-specific setting takes precedence, so IDs that have one are left to the checks above.
        /// </summary>
        private void AuditCategoryAndBulkSettings()
        {
            var provider = _ctx.Options.AnalyzerConfigOptionsProvider;
            var global = provider.GlobalOptions;
            var settingKeys = new[]
            {
                CategoryKey(ProofCategory),
                CategoryKey(ProofCheckWeakened.Category),
                BulkKey
            };

            foreach (var key in settingKeys)
            {
                global.TryGetValue(key, out var globalValue);
                if (globalValue is not null)
                {
                    ReportCategorySetting(key, globalValue, tree: null, files: null);
                }

                var perFile = new SortedDictionary<string, List<SyntaxTree>>(StringComparer.Ordinal);
                foreach (var tree in _verifiableTrees)
                {
                    if (provider.GetOptions(tree).TryGetValue(key, out var treeValue) &&
                        !string.Equals(treeValue, globalValue, StringComparison.Ordinal))
                    {
                        if (!perFile.TryGetValue(treeValue, out var list))
                        {
                            list = new List<SyntaxTree>();
                            perFile[treeValue] = list;
                        }

                        list.Add(tree);
                    }
                }

                foreach (var entry in perFile)
                {
                    ReportCategorySetting(key, entry.Key, entry.Value[0], entry.Value);
                }
            }
        }

        private void ReportCategorySetting(string key, string rawValue, SyntaxTree? tree, List<SyntaxTree>? files)
        {
            var severity = SuppressionKernel.ParseSeverity(rawValue.Trim().ToLowerInvariant());
            var affected = GuardedIds
                .Where(id => CoveredBy(key, id) && !HasIdSpecificSetting(id, tree) && !HasCategorySettingBelowBulk(key, id, tree))
                .Where(id => Weakens(id, severity))
                .ToList();
            if (affected.Count == 0)
            {
                return;
            }

            var setting = $"{DisplayKey(key)} = {SeverityWord(severity)}";
            var scope = files is null ? "the whole project" : FormatFiles(files.Select(FileName).ToList());
            var source = files is null ? "a global analyzer config" : "an `.editorconfig` section";
            var explicitFix = string.Join(", ", affected.Select(id => $"`dotnet_diagnostic.{id}.severity = {(id == SuppressionKernel.AuditId ? "warning" : "error")}`"));
            AddProjectWide(
                "d|" + key + "|" + scope,
                $"Proof check weakened: `{setting}` lowers {string.Join(", ", affected)} in {scope}. Remove it, or add dotnet_diagnostic.<ID>.severity = error for each proof ID.",
                $"{Capitalize(source)} sets `{setting}`. That lowers {DescribeAll(affected)} in {scope}, so code with these problems builds as if it were proved.",
                $"Remove `{setting}`, or keep it and add {explicitFix}; a setting for one ID wins over a category or all-analyzers setting.");
        }

        private static bool CoveredBy(string key, string id) =>
            key == BulkKey ||
            (key == CategoryKey(ProofCategory) && SuppressionKernel.IsProofCheckId(id)) ||
            (key == CategoryKey(ProofCheckWeakened.Category) && id == SuppressionKernel.AuditId);

        /// <summary>A category setting beats the all-analyzers setting, so the latter does not apply where one exists.</summary>
        private bool HasCategorySettingBelowBulk(string key, string id, SyntaxTree? tree)
        {
            if (key != BulkKey)
            {
                return false;
            }

            var categoryKey = CategoryKey(SuppressionKernel.IsProofCheckId(id) ? ProofCategory : ProofCheckWeakened.Category);
            var options = tree is null
                ? _ctx.Options.AnalyzerConfigOptionsProvider.GlobalOptions
                : _ctx.Options.AnalyzerConfigOptionsProvider.GetOptions(tree);
            return options.TryGetValue(categoryKey, out _);
        }

        private bool HasIdSpecificSetting(string id, SyntaxTree? tree)
        {
            if (_ctx.Compilation.Options.SpecificDiagnosticOptions.ContainsKey(id))
            {
                return true;
            }

            if (_treeOptions is null)
            {
                return false;
            }

            if (_treeOptions.TryGetGlobalDiagnosticValue(id, _ctx.CancellationToken, out _))
            {
                return true;
            }

            var trees = tree is null ? (IEnumerable<SyntaxTree>)_verifiableTrees : [tree];
            return trees.All(t => _treeOptions.TryGetDiagnosticValue(t, id, _ctx.CancellationToken, out _)) && trees.Any();
        }

        private static string CategoryKey(string category) =>
            "dotnet_analyzer_diagnostic.category-" + category.ToLowerInvariant() + ".severity";

        private static string DisplayKey(string key) =>
            key == CategoryKey(ProofCategory) ? "dotnet_analyzer_diagnostic.category-" + ProofCategory + ".severity"
            : key == CategoryKey(ProofCheckWeakened.Category) ? "dotnet_analyzer_diagnostic.category-" + ProofCheckWeakened.Category + ".severity"
            : key;

        /// <summary>Whether VER107 itself could be hidden at a location in this file, so a located report might not be seen.</summary>
        private bool AuditCanBeHiddenIn(SyntaxTree tree)
        {
            if (_pragmas.Any(p => p.Tree == tree && (p.IsBare || p.Codes.Contains(SuppressionKernel.AuditId))))
            {
                return true;
            }

            if (_attributes.Any(a => SuppressionKernel.SuppressMessageTargetsGuardedId(a.CheckId) && IdOf(a.CheckId) == SuppressionKernel.AuditId) ||
                AssemblyAttributes().Any(a => IdOf(a.CheckId) == SuppressionKernel.AuditId))
            {
                return true;
            }

            return _treeOptions is not null &&
                   _treeOptions.TryGetDiagnosticValue(tree, SuppressionKernel.AuditId, _ctx.CancellationToken, out var report) &&
                   Weakens(SuppressionKernel.AuditId, ToConfigured(report));
        }

        private void AuditPragmas()
        {
            foreach (var pragma in _pragmas)
            {
                if (!_verifiableTrees.Contains(pragma.Tree))
                {
                    continue;
                }

                var ids = pragma.IsBare
                    ? GuardedIds.ToList()
                    : pragma.Codes.Where(c => Weakens(c, ConfiguredSeverity.Suppressed)).Distinct().ToList();
                if (ids.Count == 0)
                {
                    continue;
                }

                var reportAt = AuditCanBeHiddenIn(pragma.Tree) ? Location.None : pragma.Location;
                var where = reportAt == Location.None ? " at " + Where(pragma.Location) : string.Empty;
                var sortKey = "1|" + pragma.Tree.FilePath + "|" + pragma.Location.SourceSpan.Start.ToString("D9");
                if (pragma.IsBare)
                {
                    Add(pragma.Location, reportAt, sortKey,
                        $"Proof check weakened: `#pragma warning disable` with no IDs{where} also hides proof errors. List only the warnings you mean.",
                        $"`#pragma warning disable` with no IDs switches off every warning and every analyzer error from here to the end of the file or the next `#pragma warning restore`. That includes {DescribeAll(GuardedIds.Where(SuppressionKernel.IsProofCheckId))}, so code here can build without being proved.",
                        "Name the warnings you mean, for example `#pragma warning disable CS0168`, so proof checks stay on.");
                }
                else
                {
                    var list = string.Join(", ", ids);
                    Add(pragma.Location, reportAt, sortKey,
                        $"Proof check weakened: `{pragma.Text}`{where} hides {list}. Remove {list} from the pragma and fix what it reports.",
                        $"`{pragma.Text}` switches off {DescribeAll(ids)} from here to the end of the file or the next `#pragma warning restore`. Code here with this problem builds as if it were proved.",
                        $"Remove {list} from the pragma, then fix what it reports. To leave a member out of verification on purpose, mark it `[Unverifiable]` instead.");
                }
            }
        }

        private IEnumerable<AttributeFinding> AssemblyAttributes()
        {
            var compilation = _ctx.Compilation;
            foreach (var attribute in compilation.Assembly.GetAttributes().Concat(compilation.SourceModule.GetAttributes()))
            {
                if (TryGetSuppressedId(attribute, out var checkId) && attribute.ApplicationSyntaxReference is { } appRef)
                {
                    yield return new AttributeFinding(checkId, appRef.GetSyntax(_ctx.CancellationToken).GetLocation(), "assembly");
                }
            }
        }

        private void AuditAttributes()
        {
            foreach (var attribute in _attributes.Concat(AssemblyAttributes()))
            {
                var id = IdOf(attribute.CheckId);
                var tree = attribute.Location.SourceTree;
                var reportAt = tree is null || AuditCanBeHiddenIn(tree) ? Location.None : attribute.Location;
                var where = reportAt == Location.None ? " at " + Where(attribute.Location) : string.Empty;
                var isAssembly = attribute.Target == "assembly";
                var what = isAssembly ? "[assembly: SuppressMessage]" : "[SuppressMessage]";
                var scope = isAssembly ? "the members it targets (or, with no target, the whole project)" : $"`{attribute.Target}`";
                Add(attribute.Location, reportAt,
                    "2|" + tree?.FilePath + "|" + attribute.Location.SourceSpan.Start.ToString("D9"),
                    $"Proof check weakened: {what} for \"{attribute.CheckId}\"{where} hides {id}. Remove the attribute and fix what it reports.",
                    $"A `{what}` attribute with check ID `\"{attribute.CheckId}\"` switches off {Describe(id)} in {scope}. Code there with this problem builds as if it were proved.",
                    isAssembly
                        ? "Remove the attribute (it is often in `GlobalSuppressions.cs`), then fix what it reports. To leave a member out of verification on purpose, mark it `[Unverifiable]` instead."
                        : "Remove the attribute, then fix what it reports. To leave a member out of verification on purpose, mark it `[Unverifiable]` instead.");
            }
        }

        private static string IdOf(string checkId)
        {
            var colon = checkId.IndexOf(':');
            return colon < 0 ? checkId : checkId.Substring(0, colon);
        }

        private static string FormatFiles(List<string> files)
        {
            var sorted = files.Distinct().OrderBy(f => f, StringComparer.Ordinal).ToList();
            return sorted.Count <= 3
                ? string.Join(", ", sorted)
                : string.Join(", ", sorted.Take(3)) + $" and {sorted.Count - 3} more file(s)";
        }

        private static string Capitalize(string text) =>
            text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);
    }
}
