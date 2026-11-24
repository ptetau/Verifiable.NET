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
/// Splits pure computation sandwiched between I/O or <c>await</c> statements into a static <c>[Verifiable]</c>
/// transition helper method, leaving an outer imperative shell (fixes VER006 and VER005 async sandwiches).
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(SandwichIoSplitCodeFixProvider)), Shared]
public sealed class SandwichIoSplitCodeFixProvider : CodeFixProvider
{
    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ["VER006", "VER005"];

    /// <inheritdoc />
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <inheritdoc />
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) return;

        var diagnostic = context.Diagnostics[0];
        var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
        var method = node.FirstAncestorOrSelf<MethodDeclarationSyntax>();
        if (method?.Body is null || method.Body.Statements.Count < 2) return;

        var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        if (semanticModel is null) return;

        var pureSpan = FindPureMiddleSlice(method.Body.Statements, semanticModel, context.CancellationToken);
        if (pureSpan is null) return;

        context.RegisterCodeFix(
            CodeAction.Create(
                title: "Sandwich I/O Split: extract pure computation to [Verifiable] static method",
                createChangedDocument: ct => SplitAsync(context.Document, root, method, pureSpan.Value.Start, pureSpan.Value.End, ct),
                equivalenceKey: nameof(SandwichIoSplitCodeFixProvider)),
            diagnostic);
    }

    private static (int Start, int End)? FindPureMiddleSlice(
        SyntaxList<StatementSyntax> statements,
        SemanticModel model,
        CancellationToken ct)
    {
        var isIo = new bool[statements.Count];
        for (var i = 0; i < statements.Count; i++)
        {
            isIo[i] = ContainsIoOrAwait(statements[i], model, ct);
        }

        // Prefer a pure contiguous slice that has at least one non-IO statement and at least one IO statement outside it
        if (!isIo.Any(x => x)) return null;

        var bestStart = -1;
        var bestEnd = -1;
        var curStart = -1;
        for (var i = 0; i < statements.Count; i++)
        {
            if (!isIo[i] && statements[i] is not ReturnStatementSyntax)
            {
                if (curStart < 0) curStart = i;
                if (i - curStart >= bestEnd - bestStart)
                {
                    bestStart = curStart;
                    bestEnd = i;
                }
            }
            else
            {
                curStart = -1;
            }
        }

        if (bestStart < 0) return null;
        return (bestStart, bestEnd);
    }

    private static bool ContainsIoOrAwait(StatementSyntax stmt, SemanticModel model, CancellationToken ct)
    {
        if (stmt.DescendantNodesAndSelf().OfType<AwaitExpressionSyntax>().Any())
        {
            return true;
        }

        foreach (var inv in stmt.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            if (model.GetSymbolInfo(inv, ct).Symbol is IMethodSymbol m)
            {
                var isVerified = m.GetAttributes().Any(a =>
                    a.AttributeClass?.ToDisplayString() is
                        "Verifiable.Contracts.VerifiableAttribute" or
                        "Verifiable.Contracts.PureAxiomAttribute");
                var ns = m.ContainingNamespace?.ToDisplayString() ?? string.Empty;
                var isPureBcl = ns == "System.Collections.Immutable" ||
                    m.ContainingType?.SpecialType == SpecialType.System_String ||
                    m.ContainingType?.ToDisplayString() == "System.Math";
                if (!isVerified && !isPureBcl)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static async Task<Document> SplitAsync(
        Document document,
        SyntaxNode root,
        MethodDeclarationSyntax method,
        int startIdx,
        int endIdx,
        CancellationToken ct)
    {
        var semanticModel = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
        if (semanticModel is null || method.Body is null) return document;

        var statements = method.Body.Statements;
        var firstPure = statements[startIdx];
        var lastPure = statements[endIdx];
        var dataFlow = semanticModel.AnalyzeDataFlow(firstPure, lastPure);
        if (dataFlow is null || !dataFlow.Succeeded) return document;

        var inputSyms = dataFlow.DataFlowsIn
            .Where(s => s is ILocalSymbol or IParameterSymbol)
            .Distinct(SymbolEqualityComparer.Default)
            .ToList();

        var outputSyms = dataFlow.WrittenInside
            .Intersect(dataFlow.DataFlowsOut, SymbolEqualityComparer.Default)
            .OfType<ILocalSymbol>()
            .ToList();

        // Build parameters for extracted pure core method
        var coreParams = new List<ParameterSyntax>();
        var callArgs = new List<ArgumentSyntax>();
        foreach (var sym in inputSyms)
        {
            var typeSym = sym switch
            {
                ILocalSymbol l => l.Type,
                IParameterSymbol p => p.Type,
                _ => null
            };
            if (typeSym is null) continue;

            var typeName = typeSym.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            coreParams.Add(SyntaxFactory.Parameter(SyntaxFactory.Identifier(sym.Name))
                .WithType(SyntaxFactory.ParseTypeName(typeName).WithTrailingTrivia(SyntaxFactory.Space)));
            callArgs.Add(SyntaxFactory.Argument(SyntaxFactory.IdentifierName(sym.Name)));
        }

        // Determine return type and return statement
        var coreBodyStmts = new List<StatementSyntax>();
        for (var i = startIdx; i <= endIdx; i++)
        {
            coreBodyStmts.Add(statements[i].WithoutTrivia());
        }

        TypeSyntax returnType;
        StatementSyntax? shellCallStmt;
        var coreMethodName = method.Identifier.ValueText.Replace("Async", "") + "PureCore";
        if (coreMethodName == method.Identifier.ValueText) coreMethodName += "Core";

        var callExpr = SyntaxFactory.InvocationExpression(
            SyntaxFactory.IdentifierName(coreMethodName),
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(callArgs)));

        if (outputSyms.Count == 1)
        {
            var outSym = outputSyms[0];
            var outTypeName = outSym.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            returnType = SyntaxFactory.ParseTypeName(outTypeName);
            coreBodyStmts.Add(SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName(outSym.Name)));

            var wasDeclaredInside = dataFlow.VariablesDeclared.Contains(outSym, SymbolEqualityComparer.Default);
            if (wasDeclaredInside)
            {
                shellCallStmt = SyntaxFactory.LocalDeclarationStatement(
                    SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("var"))
                        .AddVariables(
                            SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier(outSym.Name))
                                .WithInitializer(SyntaxFactory.EqualsValueClause(callExpr))));
            }
            else
            {
                shellCallStmt = SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.IdentifierName(outSym.Name),
                        callExpr));
            }
        }
        else
        {
            returnType = SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword));
            shellCallStmt = SyntaxFactory.ExpressionStatement(callExpr);
        }

        var verifiableAttrList = SyntaxFactory.AttributeList(
            SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.Attribute(SyntaxFactory.IdentifierName("Verifiable"))));

        var coreMethod = SyntaxFactory.MethodDeclaration(returnType.WithTrailingTrivia(SyntaxFactory.Space), coreMethodName)
            .AddAttributeLists(verifiableAttrList)
            .AddModifiers(
                SyntaxFactory.Token(SyntaxKind.PrivateKeyword).WithTrailingTrivia(SyntaxFactory.Space),
                SyntaxFactory.Token(SyntaxKind.StaticKeyword).WithTrailingTrivia(SyntaxFactory.Space))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(coreParams)))
            .WithBody(SyntaxFactory.Block(coreBodyStmts))
            .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.CarriageReturnLineFeed);

        // Update outer shell method: remove [Verifiable]/[VerifiableCandidate] and replace pure slice with core call
        var newShellBodyStmts = new List<StatementSyntax>();
        for (var i = 0; i < statements.Count; i++)
        {
            if (i == startIdx)
            {
                newShellBodyStmts.Add(shellCallStmt.WithTriviaFrom(statements[startIdx]));
            }
            else if (i < startIdx || i > endIdx)
            {
                newShellBodyStmts.Add(statements[i]);
            }
        }

        var filteredAttrs = new SyntaxList<AttributeListSyntax>();
        foreach (var al in method.AttributeLists)
        {
            var kept = al.Attributes.Where(a =>
                !a.Name.ToString().Contains("Verifiable")).ToList();
            if (kept.Count > 0)
            {
                filteredAttrs = filteredAttrs.Add(al.WithAttributes(SyntaxFactory.SeparatedList(kept)));
            }
        }

        var updatedShellMethod = method
            .WithAttributeLists(filteredAttrs)
            .WithBody(method.Body.WithStatements(SyntaxFactory.List(newShellBodyStmts)));

        var parentType = method.Parent as TypeDeclarationSyntax;
        if (parentType is null) return document;

        var members = parentType.Members;
        var methodIndex = members.IndexOf(method);
        var updatedMembers = members.Replace(method, updatedShellMethod).Insert(methodIndex + 1, coreMethod);
        var updatedParent = parentType.WithMembers(updatedMembers);

        return document.WithSyntaxRoot(root.ReplaceNode(parentType, updatedParent));
    }
}
