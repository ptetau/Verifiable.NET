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
/// Converts mutable classes/structs and mutable BCL collections into immutable <c>record</c>/<c>readonly record struct</c>
/// types, <c>with</c> expressions, and <c>System.Collections.Immutable</c> types (fixes VER001 and VER002).
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(FreezeMutableStateCodeFixProvider)), Shared]
public sealed class FreezeMutableStateCodeFixProvider : CodeFixProvider
{
    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ["VER001", "VER002"];

    /// <inheritdoc />
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <inheritdoc />
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) return;

        var diagnostic = context.Diagnostics[0];
        var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);

        if (diagnostic.Id == "VER001")
        {
            if (node.FirstAncestorOrSelf<AssignmentExpressionSyntax>() is { } assign &&
                assign.Left is MemberAccessExpressionSyntax &&
                assign.FirstAncestorOrSelf<ExpressionStatementSyntax>() is { } exprStmt)
            {
                context.RegisterCodeFix(
                    CodeAction.Create(
                        title: "Rewrite property mutation as immutable 'with' expression",
                        createChangedDocument: ct => RewriteAssignmentToWithAsync(context.Document, root, exprStmt, assign, ct),
                        equivalenceKey: "VER001_With"),
                    diagnostic);
                return;
            }

            if (node.FirstAncestorOrSelf<TypeDeclarationSyntax>() is { } typeDecl)
            {
                context.RegisterCodeFix(
                    CodeAction.Create(
                        title: "Freeze mutable type to immutable record with init properties",
                        createChangedDocument: ct => FreezeTypeDeclarationAsync(context.Document, root, typeDecl, ct),
                        equivalenceKey: "VER001_FreezeType"),
                    diagnostic);
            }
        }
        else if (diagnostic.Id == "VER002")
        {
            context.RegisterCodeFix(
                CodeAction.Create(
                    title: "Replace mutable collection with System.Collections.Immutable equivalent",
                    createChangedDocument: ct => ReplaceMutableCollectionsAsync(context.Document, root, node, ct),
                    equivalenceKey: "VER002_Immutable"),
                diagnostic);
        }
    }

    private static Task<Document> RewriteAssignmentToWithAsync(
        Document document,
        SyntaxNode root,
        ExpressionStatementSyntax stmt,
        AssignmentExpressionSyntax assign,
        CancellationToken _)
    {
        if (assign.Left is not MemberAccessExpressionSyntax ma) return Task.FromResult(document);

        var targetExpr = ma.Expression.WithoutTrivia();
        var propIdent = ma.Name.WithoutTrivia();
        var rhsExpr = assign.Right.WithoutTrivia();

        var withExpr = SyntaxFactory.WithExpression(
            targetExpr.WithTrailingTrivia(SyntaxFactory.Space),
            SyntaxFactory.InitializerExpression(
                SyntaxKind.WithInitializerExpression,
                SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        propIdent.WithTrailingTrivia(SyntaxFactory.Space),
                        rhsExpr.WithLeadingTrivia(SyntaxFactory.Space)))));

        var newAssign = SyntaxFactory.AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            targetExpr.WithTrailingTrivia(SyntaxFactory.Space),
            withExpr.WithLeadingTrivia(SyntaxFactory.Space));

        var newStmt = SyntaxFactory.ExpressionStatement(newAssign)
            .NormalizeWhitespace()
            .WithTriviaFrom(stmt);
        return Task.FromResult(document.WithSyntaxRoot(root.ReplaceNode(stmt, newStmt)));
    }

    private static Task<Document> FreezeTypeDeclarationAsync(
        Document document,
        SyntaxNode root,
        TypeDeclarationSyntax typeDecl,
        CancellationToken _)
    {
        // Rewrite all set; -> init; accessors on properties and convert class/struct to immutable record
        var updatedMembers = new SyntaxList<MemberDeclarationSyntax>();
        foreach (var member in typeDecl.Members)
        {
            if (member is PropertyDeclarationSyntax prop && prop.AccessorList is not null)
            {
                var newAccessors = new SyntaxList<AccessorDeclarationSyntax>();
                foreach (var acc in prop.AccessorList.Accessors)
                {
                    if (acc.IsKind(SyntaxKind.SetAccessorDeclaration))
                    {
                        var initAcc = SyntaxFactory.AccessorDeclaration(
                            SyntaxKind.InitAccessorDeclaration,
                            acc.AttributeLists,
                            acc.Modifiers,
                            SyntaxFactory.Token(SyntaxKind.InitKeyword).WithTriviaFrom(acc.Keyword),
                            acc.Body,
                            acc.ExpressionBody,
                            acc.SemicolonToken);
                        newAccessors = newAccessors.Add(initAcc);
                    }
                    else
                    {
                        newAccessors = newAccessors.Add(acc);
                    }
                }
                updatedMembers = updatedMembers.Add(prop.WithAccessorList(prop.AccessorList.WithAccessors(newAccessors)));
            }
            else if (member is FieldDeclarationSyntax fld && !fld.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword) || m.IsKind(SyntaxKind.ConstKeyword)))
            {
                updatedMembers = updatedMembers.Add(fld.AddModifiers(SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword).WithTrailingTrivia(SyntaxFactory.Space)));
            }
            else
            {
                updatedMembers = updatedMembers.Add(member);
            }
        }

        MemberDeclarationSyntax frozenDecl;
        if (typeDecl is StructDeclarationSyntax st)
        {
            var mods = st.Modifiers;
            if (!mods.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword)))
            {
                mods = mods.Add(SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword).WithTrailingTrivia(SyntaxFactory.Space));
            }
            frozenDecl = SyntaxFactory.RecordDeclaration(
                    SyntaxKind.RecordStructDeclaration,
                    st.AttributeLists,
                    mods,
                    SyntaxFactory.Token(SyntaxKind.RecordKeyword).WithTrailingTrivia(SyntaxFactory.Space),
                    SyntaxFactory.Token(SyntaxKind.StructKeyword).WithTrailingTrivia(SyntaxFactory.Space),
                    st.Identifier,
                    st.TypeParameterList,
                    null,
                    st.BaseList,
                    st.ConstraintClauses,
                    st.OpenBraceToken,
                    updatedMembers,
                    st.CloseBraceToken,
                    st.SemicolonToken)
                .WithTriviaFrom(st);
        }
        else if (typeDecl is ClassDeclarationSyntax cls)
        {
            var mods = cls.Modifiers;
            if (!mods.Any(m => m.IsKind(SyntaxKind.SealedKeyword) || m.IsKind(SyntaxKind.AbstractKeyword)))
            {
                mods = mods.Add(SyntaxFactory.Token(SyntaxKind.SealedKeyword).WithTrailingTrivia(SyntaxFactory.Space));
            }
            frozenDecl = SyntaxFactory.RecordDeclaration(
                    SyntaxKind.RecordDeclaration,
                    cls.AttributeLists,
                    mods,
                    SyntaxFactory.Token(SyntaxKind.RecordKeyword).WithTrailingTrivia(SyntaxFactory.Space),
                    default,
                    cls.Identifier,
                    cls.TypeParameterList,
                    null,
                    cls.BaseList,
                    cls.ConstraintClauses,
                    cls.OpenBraceToken,
                    updatedMembers,
                    cls.CloseBraceToken,
                    cls.SemicolonToken)
                .WithTriviaFrom(cls);
        }
        else
        {
            frozenDecl = typeDecl.WithMembers(updatedMembers);
        }

        return Task.FromResult(document.WithSyntaxRoot(root.ReplaceNode(typeDecl, frozenDecl)));
    }

    private static Task<Document> ReplaceMutableCollectionsAsync(
        Document document,
        SyntaxNode root,
        SyntaxNode diagnosticNode,
        CancellationToken _)
    {
        var scopeNode = (SyntaxNode?)diagnosticNode.FirstAncestorOrSelf<MethodDeclarationSyntax>()
            ?? diagnosticNode.FirstAncestorOrSelf<TypeDeclarationSyntax>()
            ?? root;

        var rewriter = new MutableCollectionRewriter();
        var updatedScope = rewriter.Visit(scopeNode)!;
        var newRoot = root.ReplaceNode(scopeNode, updatedScope);

        if (newRoot is CompilationUnitSyntax cu &&
            !cu.Usings.Any(u => u.Name?.ToString() == "System.Collections.Immutable"))
        {
            var usingDirective = SyntaxFactory.UsingDirective(
                SyntaxFactory.ParseName("System.Collections.Immutable"))
                .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);
            newRoot = cu.AddUsings(usingDirective);
        }

        return Task.FromResult(document.WithSyntaxRoot(newRoot));
    }

    private sealed class MutableCollectionRewriter : CSharpSyntaxRewriter
    {
        private static readonly Dictionary<string, string> Map = new(StringComparer.Ordinal)
        {
            ["List"] = "ImmutableList",
            ["Dictionary"] = "ImmutableDictionary",
            ["HashSet"] = "ImmutableHashSet"
        };

        public override SyntaxNode? VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
        {
            var visited = (ObjectCreationExpressionSyntax)base.VisitObjectCreationExpression(node)!;
            if (visited.Type is GenericNameSyntax gn && Map.ContainsValue(gn.Identifier.ValueText) &&
                (visited.ArgumentList is null || visited.ArgumentList.Arguments.Count == 0) &&
                visited.Initializer is null)
            {
                return SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    gn.WithoutTrivia(),
                    SyntaxFactory.IdentifierName("Empty")).WithTriviaFrom(visited);
            }
            return visited;
        }

        public override SyntaxNode? VisitArrayCreationExpression(ArrayCreationExpressionSyntax node)
        {
            var visited = (ArrayCreationExpressionSyntax)base.VisitArrayCreationExpression(node)!;
            var elemType = visited.Type.ElementType.WithoutTrivia();
            var immArrType = SyntaxFactory.GenericName(
                SyntaxFactory.Identifier("ImmutableArray"),
                SyntaxFactory.TypeArgumentList(SyntaxFactory.SingletonSeparatedList(elemType)));

            if (visited.Initializer is { Expressions.Count: > 0 } init)
            {
                return SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("ImmutableArray"),
                        SyntaxFactory.GenericName(
                            SyntaxFactory.Identifier("Create"),
                            SyntaxFactory.TypeArgumentList(SyntaxFactory.SingletonSeparatedList(elemType)))),
                    SyntaxFactory.ArgumentList(
                        SyntaxFactory.SeparatedList(init.Expressions.Select(e => SyntaxFactory.Argument(e.WithoutTrivia())))))
                    .WithTriviaFrom(visited);
            }

            return SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                immArrType,
                SyntaxFactory.IdentifierName("Empty")).WithTriviaFrom(visited);
        }

        public override SyntaxNode? VisitGenericName(GenericNameSyntax node)
        {
            var visited = (GenericNameSyntax)base.VisitGenericName(node)!;
            if (Map.TryGetValue(visited.Identifier.ValueText, out var immName))
            {
                return visited.WithIdentifier(SyntaxFactory.Identifier(immName).WithTriviaFrom(visited.Identifier));
            }
            return visited;
        }

        public override SyntaxNode? VisitArrayType(ArrayTypeSyntax node)
        {
            if (node.Parent is ArrayCreationExpressionSyntax)
            {
                return base.VisitArrayType(node);
            }

            var elem = (TypeSyntax)Visit(node.ElementType)!;
            return SyntaxFactory.GenericName(
                SyntaxFactory.Identifier("ImmutableArray"),
                SyntaxFactory.TypeArgumentList(SyntaxFactory.SingletonSeparatedList(elem.WithoutTrivia())))
                .WithTriviaFrom(node);
        }
    }
}
