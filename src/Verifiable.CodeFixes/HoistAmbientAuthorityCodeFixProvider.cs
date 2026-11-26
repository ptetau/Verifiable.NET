using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Simplification;

namespace Verifiable.CodeFixes;

/// <summary>
/// Hoists ambient authority calls (<c>DateTime.UtcNow</c>, <c>DateTimeOffset.UtcNow</c>, <c>Guid.NewGuid()</c>)
/// into explicit method parameters for eager reads in private methods whose direct callers
/// can all be updated in the same document (fixes VER004).
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
        if (method is null || !IsEagerRead(method, ambientExpr) || !context.Document.GetLinkedDocumentIds().IsEmpty) return;

        var ct = context.CancellationToken;
        var model = await context.Document.GetSemanticModelAsync(ct).ConfigureAwait(false);
        var symbol = model?.GetDeclaredSymbol(method, ct);
        if (model is null || symbol is null || symbol.DeclaredAccessibility != Accessibility.Private ||
            symbol.MethodKind != MethodKind.Ordinary || symbol.IsPartialDefinition || symbol.PartialDefinitionPart is not null ||
            SymbolEqualityComparer.Default.Equals(symbol, model.Compilation.GetEntryPoint(ct)) ||
            symbol.Parameters.Any(p => p.IsOptional || p.IsParams) ||
            symbol.ContainingType.GetMembers(symbol.Name).Length != 1) return;

        var member = model.GetSymbolInfo(ambientExpr, ct).Symbol;
        var type = member?.ContainingType;
        if (type?.ToDisplayString() is not ("System.DateTime" or "System.DateTimeOffset" or "System.Guid") ||
            member is null || !member.IsStatic) return;

        var callers = new HashSet<InvocationExpressionSyntax>();
        var references = await SymbolFinder.FindReferencesAsync(symbol, context.Document.Project.Solution, ct).ConfigureAwait(false);
        foreach (var reference in references.SelectMany(r => r.Locations))
        {
            if (reference.Document.Id != context.Document.Id || reference.IsImplicit || reference.IsCandidateLocation) return;
            var name = root.FindNode(reference.Location.SourceSpan, getInnermostNodeForTie: true)
                .FirstAncestorOrSelf<SimpleNameSyntax>();
            var target = name?.Parent is MemberAccessExpressionSyntax access && access.Name == name ? access :
                name?.Parent is MemberBindingExpressionSyntax binding && binding.Name == name ? (ExpressionSyntax)binding : name;
            if (target?.Parent is not InvocationExpressionSyntax call || call.Expression != target ||
                method.Span.Contains(call.Span) ||
                !SymbolEqualityComparer.Default.Equals((model.GetSymbolInfo(call, ct).Symbol as IMethodSymbol)?.OriginalDefinition, symbol)) return;
            callers.Add(call);
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                title: "Hoist ambient authority into explicit parameter",
                createChangedDocument: _ => Task.FromResult(Hoist(context.Document, root, method, ambientExpr, type, member.Name, callers)),
                equivalenceKey: nameof(HoistAmbientAuthorityCodeFixProvider)),
            diagnostic);
    }

    private static bool IsEagerRead(MethodDeclarationSyntax method, ExpressionSyntax read)
    {
        // Iterator bodies don't run at invocation. Otherwise accept only an unconditional
        // read at entry, never a read nested in a loop, callback, query, or branch.
        if (method.DescendantNodes().OfType<YieldStatementSyntax>().Any()) return false;
        ExpressionSyntax expr = read;
        while (expr.Parent is ParenthesizedExpressionSyntax ||
               expr.Parent is MemberAccessExpressionSyntax access && access.Expression == expr ||
               expr.Parent is InvocationExpressionSyntax invocation && invocation.Expression == expr)
            expr = (ExpressionSyntax)expr.Parent;
        if (method.ExpressionBody?.Expression == expr) return true;
        var first = method.Body?.Statements.FirstOrDefault();
        return first is ReturnStatementSyntax ret && ret.Expression == expr ||
               first is LocalDeclarationStatementSyntax local &&
               local.Declaration.Variables.First().Initializer?.Value == expr;
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

    private static Document Hoist(
        Document document,
        SyntaxNode root,
        MethodDeclarationSyntax method,
        ExpressionSyntax ambientExpr,
        INamedTypeSymbol type,
        string memberName,
        HashSet<InvocationExpressionSyntax> callers)
    {
        var isGuid = ambientExpr is InvocationExpressionSyntax;
        var paramTypeText = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var baseName = isGuid ? "id" : "now";
        var existingNames = new HashSet<string>(
            method.DescendantTokens().Where(t => t.IsKind(SyntaxKind.IdentifierToken)).Select(t => t.ValueText),
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
            .WithType(SyntaxFactory.ParseTypeName(paramTypeText).WithAdditionalAnnotations(Simplifier.Annotation)
                .WithTrailingTrivia(SyntaxFactory.Space));
        newMethod = newMethod.AddParameterListParameters(newParam);

        // 2. Replace method & update immediate call sites in the same document
        var access = SyntaxFactory.ParseExpression($"{paramTypeText}.{memberName}")
            .WithAdditionalAnnotations(Simplifier.Annotation);
        var value = isGuid ? SyntaxFactory.InvocationExpression(access) : access;
        var updatedRoot = root.ReplaceNodes(callers.Cast<SyntaxNode>().Concat(new[] { method }), (orig, rewritten) =>
        {
            if (orig == method) return newMethod;
            var call = (InvocationExpressionSyntax)rewritten;
            var arg = SyntaxFactory.Argument(value);
            if (call.ArgumentList.Arguments.Any(a => a.NameColon is not null))
                arg = arg.WithNameColon(SyntaxFactory.NameColon(paramName));
            return call.AddArgumentListArguments(arg);
        });
        return document.WithSyntaxRoot(updatedRoot);
    }
}
