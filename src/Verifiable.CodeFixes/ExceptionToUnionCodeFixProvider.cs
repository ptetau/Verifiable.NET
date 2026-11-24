using System;
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
/// Converts <c>throw</c> domain error branches inside verifiable methods into explicit <c>Result&lt;T, string&gt;.Fail(...)</c>
/// return unions (fixes VER005).
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(ExceptionToUnionCodeFixProvider)), Shared]
public sealed class ExceptionToUnionCodeFixProvider : CodeFixProvider
{
    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ["VER005"];

    /// <inheritdoc />
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <inheritdoc />
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) return;

        var diagnostic = context.Diagnostics[0];
        var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
        var throwStmt = node.FirstAncestorOrSelf<ThrowStatementSyntax>();
        if (throwStmt is null) return;

        var method = throwStmt.FirstAncestorOrSelf<MethodDeclarationSyntax>();
        if (method is null) return;

        context.RegisterCodeFix(
            CodeAction.Create(
                title: "Convert exception throw to explicit Result<T, string> return union",
                createChangedDocument: ct => ConvertThrowAsync(context.Document, root, method, ct),
                equivalenceKey: nameof(ExceptionToUnionCodeFixProvider)),
            diagnostic);
    }

    private static async Task<Document> ConvertThrowAsync(
        Document document,
        SyntaxNode root,
        MethodDeclarationSyntax method,
        CancellationToken ct)
    {
        var semanticModel = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
        if (semanticModel is null) return document;

        var methodSymbol = semanticModel.GetDeclaredSymbol(method, ct);
        if (methodSymbol is null) return document;

        var retType = methodSymbol.ReturnType;
        var alreadyResult = retType is INamedTypeSymbol nr &&
                            nr.OriginalDefinition.ToDisplayString() == "Verifiable.Contracts.Result<T, TError>";

        string resultTypeText;
        bool isVoid = methodSymbol.ReturnsVoid;
        if (alreadyResult)
        {
            resultTypeText = method.ReturnType.ToString();
        }
        else if (isVoid)
        {
            resultTypeText = "Result<Unit, string>";
        }
        else
        {
            resultTypeText = $"Result<{method.ReturnType.WithoutTrivia()}, string>";
        }

        var rewriter = new ThrowToResultRewriter(resultTypeText, wrapReturns: !alreadyResult);
        var updatedMethod = (MethodDeclarationSyntax)rewriter.Visit(method)!;

        if (!alreadyResult)
        {
            updatedMethod = updatedMethod.WithReturnType(
                SyntaxFactory.ParseTypeName(resultTypeText).WithTriviaFrom(method.ReturnType));
            if (isVoid && updatedMethod.Body is { } body &&
                (body.Statements.Count == 0 || body.Statements.Last() is not ReturnStatementSyntax))
            {
                var okUnitStmt = SyntaxFactory.ReturnStatement(
                    SyntaxFactory.ParseExpression($"{resultTypeText}.Ok(Unit.Value)").WithLeadingTrivia(SyntaxFactory.Space));
                updatedMethod = updatedMethod.WithBody(body.AddStatements(okUnitStmt));
            }
        }

        var newRoot = root.ReplaceNode(method, updatedMethod);
        if (newRoot is CompilationUnitSyntax cu &&
            !cu.Usings.Any(u => u.Name?.ToString() == "Verifiable.Contracts"))
        {
            var usingDirective = SyntaxFactory.UsingDirective(
                SyntaxFactory.ParseName("Verifiable.Contracts"))
                .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);
            newRoot = cu.AddUsings(usingDirective);
        }

        return document.WithSyntaxRoot(newRoot);
    }

    private sealed class ThrowToResultRewriter(string resultTypeText, bool wrapReturns) : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitThrowStatement(ThrowStatementSyntax node)
        {
            var errExpr = ExtractMessageExpression(node.Expression);
            var failCall = SyntaxFactory.ParseExpression($"{resultTypeText}.Fail({errExpr})");
            return SyntaxFactory.ReturnStatement(failCall.WithLeadingTrivia(SyntaxFactory.Space))
                .WithTriviaFrom(node);
        }

        public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node)
        {
            if (!wrapReturns) return base.VisitReturnStatement(node);

            if (node.Expression is null)
            {
                var okUnit = SyntaxFactory.ParseExpression($"{resultTypeText}.Ok(Unit.Value)");
                return node.WithExpression(okUnit.WithLeadingTrivia(SyntaxFactory.Space));
            }

            var okExpr = SyntaxFactory.ParseExpression($"{resultTypeText}.Ok({node.Expression.WithoutTrivia()})");
            return node.WithExpression(okExpr.WithLeadingTrivia(SyntaxFactory.Space));
        }

        private static string ExtractMessageExpression(ExpressionSyntax? thrown)
        {
            if (thrown is ObjectCreationExpressionSyntax obj &&
                obj.ArgumentList?.Arguments.Count > 0)
            {
                return obj.ArgumentList.Arguments[0].Expression.WithoutTrivia().ToString();
            }

            if (thrown is ImplicitObjectCreationExpressionSyntax impObj &&
                impObj.ArgumentList?.Arguments.Count > 0)
            {
                return impObj.ArgumentList.Arguments[0].Expression.WithoutTrivia().ToString();
            }

            var exName = thrown is ObjectCreationExpressionSyntax oc ? oc.Type.ToString() : "DomainError";
            return $"\"{exName}\"";
        }
    }
}
