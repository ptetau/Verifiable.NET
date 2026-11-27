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
/// Splits a dense <c>Spec.Requires</c>, <c>Spec.RequiresWhen</c>, <c>Spec.Ensures</c>, or <c>Spec.EnsuresWhen</c>
/// statement containing multiple top-level <c>&amp;&amp;</c> conjuncts into individual specification calls (fixes VER301).
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(SplitSpecClauseCodeFixProvider)), Shared]
public sealed class SplitSpecClauseCodeFixProvider : CodeFixProvider
{
    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ["VER301"];

    /// <inheritdoc />
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <inheritdoc />
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var diagnostic = context.Diagnostics[0];
        if (!diagnostic.Properties.TryGetValue("FixKind", out var fixKind) ||
            fixKind != "VER301_SplitConjuncts")
        {
            return;
        }

        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) return;

        var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
        var invocation = node.FirstAncestorOrSelf<InvocationExpressionSyntax>();
        if (invocation is null || invocation.Parent is not ExpressionStatementSyntax stmt || stmt.Parent is not BlockSyntax)
        {
            return;
        }

        if (!TryGetSplitPlan(invocation, out var plan))
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                title: "Split Spec clause into separate statements",
                createChangedDocument: ct => SplitAsync(context.Document, stmt, invocation, plan!, ct),
                equivalenceKey: nameof(SplitSpecClauseCodeFixProvider)),
            diagnostic);
    }

    private sealed record SplitPlan(
        int PredicateArgIndex,
        LambdaExpressionSyntax? EnclosingLambda,
        List<ExpressionSyntax> Conjuncts);

    private static bool TryGetSplitPlan(InvocationExpressionSyntax invocation, out SplitPlan? plan)
    {
        plan = null;
        var methodName = invocation.Expression switch
        {
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
            SimpleNameSyntax sn => sn.Identifier.ValueText,
            _ => null
        };
        if (methodName is not ("Requires" or "RequiresWhen" or "Ensures" or "EnsuresWhen"))
        {
            return false;
        }

        var predicateIdx = methodName is "RequiresWhen" or "EnsuresWhen" ? 1 : 0;
        if (invocation.ArgumentList.Arguments.Count <= predicateIdx)
        {
            return false;
        }

        var argExpr = invocation.ArgumentList.Arguments[predicateIdx].Expression;
        LambdaExpressionSyntax? lambda = null;
        ExpressionSyntax bodyExpr;

        if (argExpr is SimpleLambdaExpressionSyntax { ExpressionBody: { } simpleBody } simpleLambda)
        {
            lambda = simpleLambda;
            bodyExpr = simpleBody;
        }
        else if (argExpr is ParenthesizedLambdaExpressionSyntax { ExpressionBody: { } parenBody } parenLambda)
        {
            lambda = parenLambda;
            bodyExpr = parenBody;
        }
        else
        {
            bodyExpr = argExpr;
        }

        var conjuncts = new List<ExpressionSyntax>();
        FlattenConjuncts(bodyExpr, conjuncts);
        if (conjuncts.Count < 2)
        {
            return false;
        }

        plan = new SplitPlan(predicateIdx, lambda, conjuncts);
        return true;
    }

    private static void FlattenConjuncts(ExpressionSyntax expr, List<ExpressionSyntax> output)
    {
        var cur = UnwrapParens(expr);
        if (cur is BinaryExpressionSyntax bin && bin.IsKind(SyntaxKind.LogicalAndExpression))
        {
            FlattenConjuncts(bin.Left, output);
            FlattenConjuncts(bin.Right, output);
            return;
        }

        output.Add(cur.WithoutTrivia());
    }

    private static ExpressionSyntax UnwrapParens(ExpressionSyntax expr)
    {
        while (expr is ParenthesizedExpressionSyntax paren)
        {
            expr = paren.Expression;
        }
        return expr;
    }

    private static async Task<Document> SplitAsync(
        Document document,
        ExpressionStatementSyntax originalStmt,
        InvocationExpressionSyntax originalInvocation,
        SplitPlan plan,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null) return document;

        var indentTrivia = ExtractIndentation(originalStmt.GetLeadingTrivia());
        var trailingTrivia = originalStmt.GetTrailingTrivia();
        var hasNewline = trailingTrivia.Any(t => t.IsKind(SyntaxKind.EndOfLineTrivia));
        var eolTrivia = hasNewline
            ? trailingTrivia.First(t => t.IsKind(SyntaxKind.EndOfLineTrivia))
            : SyntaxFactory.EndOfLine("\n");

        var replacementStmts = new List<StatementSyntax>(plan.Conjuncts.Count);
        for (var i = 0; i < plan.Conjuncts.Count; i++)
        {
            var conjunct = plan.Conjuncts[i];
            ExpressionSyntax newArgExpr = plan.EnclosingLambda switch
            {
                SimpleLambdaExpressionSyntax sl => sl.WithExpressionBody(
                    sl.ArrowToken.TrailingTrivia.Count > 0 ? conjunct : conjunct.WithLeadingTrivia(SyntaxFactory.Space)),
                ParenthesizedLambdaExpressionSyntax pl => pl.WithExpressionBody(
                    pl.ArrowToken.TrailingTrivia.Count > 0 ? conjunct : conjunct.WithLeadingTrivia(SyntaxFactory.Space)),
                _ => conjunct
            };

            var origArg = originalInvocation.ArgumentList.Arguments[plan.PredicateArgIndex];
            var newArg = origArg.WithExpression(newArgExpr.WithTriviaFrom(origArg.Expression));
            var newArgs = originalInvocation.ArgumentList.Arguments.Replace(origArg, newArg);
            var newInv = originalInvocation.WithArgumentList(originalInvocation.ArgumentList.WithArguments(newArgs));

            var stmt = originalStmt.WithExpression(newInv);
            stmt = i == 0
                ? stmt.WithLeadingTrivia(originalStmt.GetLeadingTrivia())
                : stmt.WithLeadingTrivia(indentTrivia);

            stmt = i == plan.Conjuncts.Count - 1
                ? stmt.WithTrailingTrivia(trailingTrivia)
                : stmt.WithTrailingTrivia(eolTrivia);

            replacementStmts.Add(stmt);
        }

        var newRoot = root.ReplaceNode(originalStmt, replacementStmts);
        return document.WithSyntaxRoot(newRoot);
    }

    private static SyntaxTriviaList ExtractIndentation(SyntaxTriviaList leadingTrivia)
    {
        // Keep only the whitespace trivia on the same line as the statement (after the last newline)
        for (var i = leadingTrivia.Count - 1; i >= 0; i--)
        {
            if (leadingTrivia[i].IsKind(SyntaxKind.EndOfLineTrivia))
            {
                return SyntaxFactory.TriviaList(leadingTrivia.Skip(i + 1));
            }
        }

        return SyntaxFactory.TriviaList(leadingTrivia.Where(t => t.IsKind(SyntaxKind.WhitespaceTrivia)));
    }
}
