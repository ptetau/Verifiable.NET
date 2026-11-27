using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Verifiable.Analyzers.Smt;

/// <summary>
/// Roslyn <see cref="DiagnosticSuppressor"/> that suppresses redundant C# nullable warnings
/// (<c>CS8602</c> / <c>CS8604</c>) inside <c>[Verifiable]</c> methods whenever Z3 formally proves
/// the flagged expression is non-null along every reaching path (e.g., via <c>Spec.Requires</c>
/// or <c>[Invariant]</c>).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class VerifiableNullabilitySuppressor : DiagnosticSuppressor
{
    internal static readonly SuppressionDescriptor DereferenceProvedNonNull = new(
        id: "VER_SUP001",
        suppressedDiagnosticId: "CS8602",
        justification: "Expression is formally proved non-null by Verifiable.NET Z3 SMT verification.");

    internal static readonly SuppressionDescriptor ArgumentProvedNonNull = new(
        id: "VER_SUP002",
        suppressedDiagnosticId: "CS8604",
        justification: "Argument is formally proved non-null by Verifiable.NET Z3 SMT verification.");

    /// <inheritdoc />
    public override ImmutableArray<SuppressionDescriptor> SupportedSuppressions { get; } =
        [DereferenceProvedNonNull, ArgumentProvedNonNull];

    /// <inheritdoc />
    public override void ReportSuppressions(SuppressionAnalysisContext context)
    {
        string? msbuildZ3Path = null;
        context.Options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue(
            "build_property.VerifiableZ3Path",
            out msbuildZ3Path);

        var closedEnums = false;
        if (context.Options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue(
                "build_property.VerifiableClosedEnums",
                out var closedEnumsVal) &&
            string.Equals(closedEnumsVal, "true", System.StringComparison.OrdinalIgnoreCase))
        {
            closedEnums = true;
        }

        foreach (var diagnostic in context.ReportedDiagnostics)
        {
            var descriptor = diagnostic.Id switch
            {
                "CS8602" => DereferenceProvedNonNull,
                "CS8604" => ArgumentProvedNonNull,
                _ => null
            };
            if (descriptor is null) continue;

            var loc = diagnostic.Location;
            if (!loc.IsInSource || loc.SourceTree is null) continue;

            var root = loc.SourceTree.GetRoot(context.CancellationToken);
            var node = root.FindNode(loc.SourceSpan, getInnermostNodeForTie: true);
            var methodDecl = node.FirstAncestorOrSelf<MethodDeclarationSyntax>();
            if (methodDecl is null) continue;

            var semanticModel = context.GetSemanticModel(loc.SourceTree);
            if (semanticModel.GetDeclaredSymbol(methodDecl, context.CancellationToken) is not IMethodSymbol method)
            {
                continue;
            }

            if (VerifiableSymbolHelpers.GetScope(method) != VerificationScope.Verifiable ||
                VerifiableSymbolHelpers.IsSynthesizedRecordMember(method) ||
                VerifiableSymbolHelpers.IsGeneratedByVerifiableGenerators(method) ||
                VerifiableSymbolHelpers.HasAttribute(method, VerifiableSymbolHelpers.PureAxiomAttr) ||
                VerifiableSymbolHelpers.HasAttribute(method, VerifiableSymbolHelpers.GhostAttr))
            {
                continue;
            }

            var bodyOp = semanticModel.GetOperation(methodDecl, context.CancellationToken);
            var cfg = bodyOp switch
            {
                IMethodBodyOperation mbo => ControlFlowGraph.Create(mbo),
                IBlockOperation blk => ControlFlowGraph.Create(blk),
                _ => null
            };
            if (cfg is null || bodyOp is null) continue;

            var lowerer = new CfgSsaLowerer(context.Compilation, method, closedEnums: closedEnums);
            var outcome = lowerer.LowerNonNullChecks(cfg, [bodyOp], loc.SourceSpan);

            // Fail closed: require at least one lowered check and zero lowering errors.
            if (outcome.Errors.Count > 0 || outcome.Obligations.Count == 0)
            {
                continue;
            }

            var allProvedNonNull = true;
            foreach (var ob in outcome.Obligations)
            {
                var script = VerifiableSmtAnalyzer.BuildQueryScript(ob);
                var z3Res = Z3SolverRunner.Execute(script, msbuildZ3Path, loc.SourceTree.FilePath);
                if (z3Res.Status != SmtStatus.Unsat)
                {
                    allProvedNonNull = false;
                    break;
                }
            }

            if (allProvedNonNull)
            {
                context.ReportSuppression(Suppression.Create(descriptor, diagnostic));
            }
        }
    }
}
