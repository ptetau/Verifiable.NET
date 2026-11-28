using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace Verifiable.Analyzers;

/// <summary>
/// Spec traceability: reports <c>VER201</c>-<c>VER204</c> for the <c>clause</c> IDs on contracts and constrained types
/// against the <c>verifiable.clauses.json</c> manifest (an <c>AdditionalFiles</c> item). It never changes a proof.
/// Without a manifest only <c>VER204</c> (an unusable clause ID) can fire.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ClauseCoverageAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        [
            VerifiableDescriptors.UnknownClause,
            VerifiableDescriptors.UncoveredClause,
            VerifiableDescriptors.MissingClause,
            VerifiableDescriptors.InvalidClause
        ];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(start =>
        {
            var constructs = new ConcurrentBag<ClauseConstruct>();

            start.RegisterOperationAction(ctx =>
            {
                if (ClauseCatalog.FromInvocation((IInvocationOperation)ctx.Operation, ctx.ContainingSymbol) is { } c)
                {
                    constructs.Add(c);
                }
            }, OperationKind.Invocation);

            start.RegisterSymbolAction(ctx =>
            {
                foreach (var c in ClauseCatalog.FromSymbol(ctx.Symbol))
                {
                    constructs.Add(c);
                }
            }, SymbolKind.NamedType, SymbolKind.Method, SymbolKind.Property);

            start.RegisterCompilationEndAction(end => Report(end, start.Options, constructs));
        });
    }

    private static void Report(CompilationAnalysisContext ctx, AnalyzerOptions options, ConcurrentBag<ClauseConstruct> bag)
    {
        var constructs = bag.ToList();

        foreach (var c in constructs.Where(c => c.Problem is not null).OrderBy(c => c.Location.SourceSpan.Start))
        {
            ctx.ReportDiagnostic(Diagnostic.Create(VerifiableDescriptors.InvalidClause, c.Location, c.Problem));
        }

        var file = options.AdditionalFiles.FirstOrDefault(f => ClauseManifest.IsManifestPath(f.Path));
        if (file is null || file.GetText(ctx.CancellationToken) is not { } text)
        {
            return;
        }

        var manifest = ClauseManifest.Parse(text.ToString());
        if (manifest.Errors.Count > 0)
        {
            foreach (var e in manifest.Errors)
            {
                ctx.ReportDiagnostic(Diagnostic.Create(VerifiableDescriptors.InvalidClause, ManifestLocation(file.Path, text, e.Start, e.Length), e.Message));
            }
            return;
        }

        var coverage = ClauseCatalog.Compare(manifest, constructs);
        foreach (var c in coverage.Unknown)
        {
            ctx.ReportDiagnostic(Diagnostic.Create(
                VerifiableDescriptors.UnknownClause,
                c.Location,
                $"`{c.SymbolDisplay}` names clause \"{c.Clause}\", which is not in the clause manifest ({ClauseManifest.FileName}). Fix the ID or add the clause to the manifest."));
        }

        foreach (var c in coverage.WithoutClause)
        {
            ctx.ReportDiagnostic(Diagnostic.Create(
                VerifiableDescriptors.MissingClause,
                c.Location,
                $"This {Describe(c.Kind)} in `{c.SymbolDisplay}` has no clause ID, so nothing links it to the spec. Add `clause: \"C1\"` (contract calls) or `Clause = \"C1\"` (attributes)."));
        }

        foreach (var clause in coverage.Uncovered)
        {
            ctx.ReportDiagnostic(Diagnostic.Create(
                VerifiableDescriptors.UncoveredClause,
                ManifestLocation(file.Path, text, clause.Start, clause.Length),
                $"Clause \"{clause.Id}\" in the manifest has no contract, type alias, refinement or invariant in this project that names it."));
        }
    }

    private static string Describe(string kind) => kind switch
    {
        "requires" => "precondition",
        "ensures" => "postcondition",
        "loopInvariant" => "loop invariant",
        "decreases" => "termination measure",
        "typeAlias" => "type alias",
        "refined" => "refined type",
        _ => "invariant",
    };

    private static Location ManifestLocation(string path, SourceText text, int start, int length)
    {
        var span = new TextSpan(Math.Min(start, text.Length), Math.Min(length, Math.Max(text.Length - start, 0)));
        return Location.Create(path, span, text.Lines.GetLinePositionSpan(span));
    }
}
