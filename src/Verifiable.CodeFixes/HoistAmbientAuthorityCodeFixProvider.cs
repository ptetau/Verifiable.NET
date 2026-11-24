using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Verifiable.CodeFixes;

/// <summary>
/// Hoists ambient authority calls (<c>DateTime.UtcNow</c>, <c>DateTimeOffset.UtcNow</c>, <c>Guid.NewGuid()</c>)
/// into explicit method parameters and updates in-document call sites (fixes VER004).
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(HoistAmbientAuthorityCodeFixProvider)), Shared]
public sealed class HoistAmbientAuthorityCodeFixProvider : CodeFixProvider
{
    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ["VER004"];

    /// <inheritdoc />
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <inheritdoc />
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) return;

        var diagnostic = context.Diagnostics[0];
        var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
        var ambientExpr = FindAmbientExpression(node);
        if (ambientExpr is null) return;

        var method = ambientExpr.FirstAncestorOrSelf<MethodDeclarationSyntax>();
        if (method is null) return;

        context.RegisterCodeFix(
            CodeAction.Create(
                title: "Hoist ambient authority into explicit parameter",
                createChangedDocument: ct => HoistAsync(context.Document, root, method, ambientExpr, ct),
                equivalenceKey: nameof(HoistAmbientAuthorityCodeFixProvider)),
            diagnostic);
    }

    private static ExpressionSyntax? FindAmbientExpression(SyntaxNode node)
    {
        for (var cur = node; cur is not null && cur is not StatementSyntax; cur = cur.Parent)
        {
            if (cur is InvocationExpressionSyntax inv &&
                inv.Expression is MemberAccessExpressionSyntax ma &&
                ma.Name.Identifier.ValueText == "NewGuid" &&
                ma.Expression.ToString().EndsWith("Guid", StringComparison.Ordinal))
            {
                return inv;
            }

            if (cur is MemberAccessExpressionSyntax mem &&
                mem.Name.Identifier.ValueText is "UtcNow" or "Now" or "Today" &&
                (mem.Expression.ToString().EndsWith("DateTime", StringComparison.Ordinal) ||
                 mem.Expression.ToString().EndsWith("DateTimeOffset", StringComparison.Ordinal)))
            {
                return mem;
            }
        }
        return null;
    }

    private static async Task<Document> HoistAsync(
        Document document,
        SyntaxNode root,
        MethodDeclarationSyntax method,
        ExpressionSyntax ambientExpr,
        CancellationToken ct)
    {
        var semanticModel = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
        if (semanticModel is null) return document;

        var methodSymbol = semanticModel.GetDeclaredSymbol(method, ct);
        var exprType = semanticModel.GetTypeInfo(ambientExpr, ct).Type;
        var isGuid = exprType?.ToDisplayString() == "System.Guid" || ambientExpr is InvocationExpressionSyntax;

        var paramTypeText = exprType is not null
            ? exprType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
            : (isGuid ? "Guid" : "DateTimeOffset");

        var baseName = isGuid ? "id" : "now";
        var existingNames = new HashSet<string>(
            method.ParameterList.Parameters.Select(p => p.Identifier.ValueText),
            StringComparer.Ordinal);
        var paramName = baseName;
        var suffix = 1;
        while (!existingNames.Add(paramName))
        {
            paramName = baseName + suffix++;
        }

        // 1. Update method: replace ambient expression with parameter identifier and append parameter
        var newMethod = method.ReplaceNode(
            ambientExpr,
            SyntaxFactory.IdentifierName(paramName).WithTriviaFrom(ambientExpr));

        var newParam = SyntaxFactory.Parameter(SyntaxFactory.Identifier(paramName))
            .WithType(SyntaxFactory.ParseTypeName(paramTypeText).WithTrailingTrivia(SyntaxFactory.Space));
        newMethod = newMethod.AddParameterListParameters(newParam);

        // 2. Replace method & update immediate call sites in the same document
        var replacementMap = new Dictionary<SyntaxNode, SyntaxNode>
        {
            [method] = newMethod
        };

        if (methodSymbol is not null)
        {
            foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (inv.FirstAncestorOrSelf<MethodDeclarationSyntax>() == method) continue;
                var target = semanticModel.GetSymbolInfo(inv, ct).Symbol;
                if (SymbolEqualityComparer.Default.Equals(target, methodSymbol))
                {
                    var arg = SyntaxFactory.Argument(ambientExpr.WithoutTrivia());
                    replacementMap[inv] = inv.AddArgumentListArguments(arg);
                }
            }
        }

        var updatedRoot = root.ReplaceNodes(replacementMap.Keys, (orig, _) => replacementMap[orig]);
        return document.WithSyntaxRoot(updatedRoot);
    }
}
