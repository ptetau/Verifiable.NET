using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

// VERIFIABLE_AGENT is read the same way as in VerifiableSmtAnalyzer, so agent runs get the JSON work items.
#pragma warning disable RS1035

namespace Verifiable.Analyzers;

/// <summary>An unwritten method body (<c>Spec.Hole</c>): where it is, and what the body must satisfy.</summary>
internal sealed record HoleInfo(
    string Symbol,
    string? Clause,
    string? ClauseText,
    string Signature,
    IReadOnlyList<string> Contracts,
    Location Location);

/// <summary>
/// Requirement R4: every <c>Spec.Hole</c> fails the build with <c>VER108</c>, naming the clause, so a body
/// the LLM has not written yet can never ship. Under <c>VERIFIABLE_AGENT=1</c> each hole also carries a
/// single-line <c>&lt;verifiable-hole&gt;</c> JSON work item: signature, contracts and clause text.
/// <c>VerifiableHolesAsWarnings=true</c> makes it a warning while working; the method is still never proved.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HoleAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(VerifiableDescriptors.UnwrittenBody);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var global = start.Options.AnalyzerConfigOptionsProvider.GlobalOptions;
            global.TryGetValue("build_property.VerifiableHolesAsWarnings", out var asWarnings);
            global.TryGetValue("build_property.VerifiableAgent", out var msbuildAgent);
            var warn = asWarnings == "1" || string.Equals(asWarnings, "true", StringComparison.OrdinalIgnoreCase);
            var envAgent = Environment.GetEnvironmentVariable("VERIFIABLE_AGENT");
            var agent = envAgent == "1" || string.Equals(envAgent, "true", StringComparison.OrdinalIgnoreCase) ||
                        msbuildAgent == "1" || string.Equals(msbuildAgent, "true", StringComparison.OrdinalIgnoreCase);
            var clauseText = Holes.ManifestTexts(start.Options.AdditionalFiles, start.CancellationToken);

            start.RegisterSyntaxNodeAction(ctx =>
            {
                var inv = (InvocationExpressionSyntax)ctx.Node;
                if (!Holes.IsHoleCall(inv, ctx.SemanticModel)) return;
                var hole = Holes.Describe(inv, clauseText);
                var props = ImmutableDictionary.CreateBuilder<string, string?>(StringComparer.Ordinal);
                props["Symbol"] = hole.Symbol;
                if (hole.Clause is not null) props["Clause"] = hole.Clause;
                props["Contract"] = string.Join("\n", hole.Contracts);
                props["Fix"] = "Write the body so it meets the contracts above, then remove Spec.Hole.";

                var summary = $"`{hole.Symbol}` has an unwritten body (Spec.Hole for clause {hole.Clause ?? "(none)"}); it is never proved.";
                var message = ElmDiagnosticFormatter.Format(
                    "UNWRITTEN BODY",
                    inv.GetLocation(),
                    $"`{hole.Symbol}` still has a `Spec.Hole` where its body should be. Signature: `{hole.Signature}`.",
                    spirit: hole.ClauseText is null ? null : $"Clause {hole.Clause}: {hole.ClauseText}",
                    contract: hole.Contracts.Count == 0 ? null : string.Join("\n", hole.Contracts),
                    explanation: "A method with a hole is never proved. Run `verifiable-query holes` for the whole work list.",
                    fixHint: "Write the body so it meets the contracts above, then remove Spec.Hole.",
                    agentPayload: agent ? "<verifiable-hole>" + Holes.ToJson(hole) + "</verifiable-hole>" : null,
                    summary: summary);

                ctx.ReportDiagnostic(Diagnostic.Create(
                    VerifiableDescriptors.UnwrittenBody,
                    inv.GetLocation(),
                    warn ? DiagnosticSeverity.Warning : DiagnosticSeverity.Error,
                    additionalLocations: null,
                    properties: props.ToImmutable(),
                    message));
            }, SyntaxKind.InvocationExpression);
        });
    }
}

/// <summary>Finds and describes <c>Spec.Hole</c> calls; shared by the analyzers and the query tool.</summary>
internal static class Holes
{
    public static bool IsHoleCall(InvocationExpressionSyntax inv, SemanticModel? model)
    {
        var name = inv.Expression switch
        {
            MemberAccessExpressionSyntax { Name: GenericNameSyntax g } => g.Identifier.ValueText,
            MemberAccessExpressionSyntax { Name: IdentifierNameSyntax i } => i.Identifier.ValueText,
            _ => null,
        };
        if (name != "Hole") return false;
        if (model?.GetSymbolInfo(inv).Symbol is IMethodSymbol m)
        {
            return m.ContainingType?.ToDisplayString() == VerifiableSymbolHelpers.SpecTypeName;
        }
        return inv.Expression is MemberAccessExpressionSyntax ma && ma.Expression.ToString() is "Spec" or "Verifiable.Contracts.Spec";
    }

    /// <summary>True when the method or accessor body around this syntax contains a hole.</summary>
    public static bool ContainsHole(SyntaxNode body) =>
        body.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Any(i => IsHoleCall(i, null));

    public static IEnumerable<InvocationExpressionSyntax> Find(SyntaxTree tree) =>
        tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Where(i => IsHoleCall(i, null));

    public static HoleInfo Describe(InvocationExpressionSyntax inv, IReadOnlyDictionary<string, string?> clauseText)
    {
        var clause = inv.ArgumentList.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax lit &&
                     lit.IsKind(SyntaxKind.StringLiteralExpression)
            ? lit.Token.ValueText
            : null;
        var member = inv.Ancestors().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault();
        var signature = member switch
        {
            MethodDeclarationSyntax m => $"{string.Join(" ", m.Modifiers.Select(x => x.ValueText))} {m.ReturnType} {m.Identifier.ValueText}{m.TypeParameterList}{m.ParameterList}".Trim(),
            ConstructorDeclarationSyntax c => $"{string.Join(" ", c.Modifiers.Select(x => x.ValueText))} {c.Identifier.ValueText}{c.ParameterList}".Trim(),
            _ => ClauseIds.SymbolOf(inv),
        };
        var contracts = member?.Body?.Statements
            .OfType<ExpressionStatementSyntax>()
            .Select(s => s.Expression)
            .OfType<InvocationExpressionSyntax>()
            .Where(c => ClauseIds.SpecMethodName(c) is not null)
            .Select(c => OneLine(c.ToString()))
            .ToList() ?? new List<string>();
        clauseText.TryGetValue(clause ?? string.Empty, out var text);
        return new HoleInfo(ClauseIds.SymbolOf(inv), clause, text, OneLine(signature), contracts, inv.GetLocation());
    }

    public static Dictionary<string, string?> ManifestTexts(ImmutableArray<AdditionalText> files, System.Threading.CancellationToken ct)
    {
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        var manifest = files.FirstOrDefault(f => f.Path.EndsWith(ClauseCoverageAnalyzer.ManifestSuffix, StringComparison.OrdinalIgnoreCase));
        var text = manifest?.GetText(ct)?.ToString();
        if (text is not null && ClauseCoverageAnalyzer.TryParseManifest(text, out var clauses, out _))
        {
            foreach (var c in clauses) map[c.Id] = c.Text;
        }
        return map;
    }

    public static string ToJson(HoleInfo h)
    {
        var sb = new StringBuilder("{\"rule\":\"VER108\"");
        sb.Append(",\"symbol\":").Append(Str(h.Symbol));
        sb.Append(",\"clause\":").Append(h.Clause is null ? "null" : Str(h.Clause));
        sb.Append(",\"clauseText\":").Append(h.ClauseText is null ? "null" : Str(h.ClauseText));
        sb.Append(",\"signature\":").Append(Str(h.Signature));
        sb.Append(",\"contracts\":[").Append(string.Join(",", h.Contracts.Select(Str))).Append(']');
        sb.Append('}');
        return sb.ToString();
    }

    private static string OneLine(string text) =>
        string.Join(" ", text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));

    private static string Str(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < 32) sb.Append("\\u").Append(((int)ch).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    else sb.Append(ch);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }
}
