using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Verifiable.Analyzers;

/// <summary>
/// Reads spec clause IDs (requirement R1) from where code can carry them: the <c>clause</c> argument
/// of a <c>Spec.*</c> contract, and the <c>Clause</c> property of <c>[TypeAlias]</c>, <c>[Refined]</c>
/// and <c>[Invariant]</c>. Syntax only, so it works the same in the analyzers and the query tool.
/// </summary>
internal static class ClauseIds
{
    /// <summary>The <c>Spec</c> methods that take a clause, with the position of their <c>clause</c> parameter.</summary>
    private static readonly Dictionary<string, int> ClausePosition = new(StringComparer.Ordinal)
    {
        ["Requires"] = 2,
        ["Ensures"] = 2,
        ["LoopInvariant"] = 2,
        ["Decreases"] = 2,
        ["RequiresWhen"] = 3,
        ["EnsuresWhen"] = 3,
    };

    private static readonly string[] ClauseAttributes = ["TypeAlias", "Refined", "Invariant"];

    /// <summary>One construct that carries (or could carry) a clause.</summary>
    internal sealed record ClauseSite(string? Clause, string Kind, string Symbol, string Text, Location Location);

    /// <summary>The <c>Spec</c> method name of a contract call, or null if the call is not one.</summary>
    public static string? SpecMethodName(InvocationExpressionSyntax inv)
    {
        var name = inv.Expression switch
        {
            MemberAccessExpressionSyntax ma when ma.Expression.ToString() is "Spec" or "Verifiable.Contracts.Spec" or "global::Verifiable.Contracts.Spec" => ma.Name,
            _ => null,
        };
        var id = name switch
        {
            GenericNameSyntax g => g.Identifier.ValueText,
            IdentifierNameSyntax i => i.Identifier.ValueText,
            _ => null,
        };
        return id is not null && ClausePosition.ContainsKey(id) ? id : null;
    }

    /// <summary>The clause a <c>Spec.*</c> contract call names, or null.</summary>
    public static string? FromSpecCall(InvocationExpressionSyntax inv)
    {
        var method = SpecMethodName(inv);
        if (method is null) return null;
        var args = inv.ArgumentList.Arguments;
        var named = args.FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == "clause");
        if (named is not null) return Literal(named.Expression);

        var position = ClausePosition[method];
        // Positional only while no earlier argument was named out of order.
        if (args.Count > position && args.Take(position + 1).All(a => a.NameColon is null))
        {
            return Literal(args[position].Expression);
        }
        return null;
    }

    /// <summary>The clause an attribute names through <c>Clause = "..."</c>, or null.</summary>
    public static string? FromAttribute(AttributeSyntax attr)
    {
        var arg = attr.ArgumentList?.Arguments.FirstOrDefault(a => a.NameEquals?.Name.Identifier.ValueText == "Clause");
        return arg is null ? null : Literal(arg.Expression);
    }

    public static bool IsClauseAttribute(AttributeSyntax attr)
    {
        var name = attr.Name switch
        {
            GenericNameSyntax g => g.Identifier.ValueText,
            IdentifierNameSyntax i => i.Identifier.ValueText,
            QualifiedNameSyntax q => q.Right.Identifier.ValueText,
            _ => attr.Name.ToString(),
        };
        if (name.EndsWith("Attribute", StringComparison.Ordinal)) name = name.Substring(0, name.Length - "Attribute".Length);
        return ClauseAttributes.Contains(name);
    }

    private static string? Literal(ExpressionSyntax expr) =>
        expr is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression) ? lit.Token.ValueText : null;

    /// <summary>
    /// The clause for a diagnostic at <paramref name="location"/>: the clause of the contract or attribute
    /// it points into, else of the rule member it sits in, else the one clause the enclosing method's
    /// contracts share (so an overflow check in a method traced to one clause reports that clause).
    /// </summary>
    public static string? ForLocation(Location? location)
    {
        if (location is null || !location.IsInSource || location.SourceTree is null) return null;
        var node = location.SourceTree.GetRoot().FindNode(location.SourceSpan, false, true);

        for (var n = node; n is not null; n = n.Parent)
        {
            if (n is InvocationExpressionSyntax inv && SpecMethodName(inv) is not null) return FromSpecCall(inv);
            if (n is AttributeSyntax attr && IsClauseAttribute(attr)) return FromAttribute(attr);
            if (n is MemberDeclarationSyntax member and not BaseTypeDeclarationSyntax)
            {
                var own = ClauseOfMember(member);
                if (own is not null) return own;
                var shared = member.DescendantNodes().OfType<InvocationExpressionSyntax>()
                    .Select(FromSpecCall).Where(c => c is not null).Distinct().ToList();
                if (shared.Count == 1) return shared[0];
                if (shared.Count > 1) return null;
            }
            if (n is BaseTypeDeclarationSyntax type) return ClauseOfAttributes(type.AttributeLists);
        }
        return null;
    }

    private static string? ClauseOfMember(MemberDeclarationSyntax member)
    {
        var own = ClauseOfAttributes(member.AttributeLists);
        if (own is not null) return own;
        // IsValid of a constrained type carries the type's clause.
        if (member is MethodDeclarationSyntax { Identifier.ValueText: "IsValid" } && member.Parent is BaseTypeDeclarationSyntax t)
        {
            return ClauseOfAttributes(t.AttributeLists);
        }
        return null;
    }

    private static string? ClauseOfAttributes(SyntaxList<AttributeListSyntax> lists) =>
        lists.SelectMany(l => l.Attributes).Where(IsClauseAttribute).Select(FromAttribute).FirstOrDefault(c => c is not null);

    /// <summary>
    /// Every contract and rule in a syntax tree, with its clause (or null), for coverage reports.
    /// </summary>
    public static IEnumerable<ClauseSite> Sites(SyntaxTree tree)
    {
        var root = tree.GetRoot();
        foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var method = SpecMethodName(inv);
            if (method is null) continue;
            yield return new ClauseSite(FromSpecCall(inv), "Spec." + method, SymbolOf(inv), inv.ToString(), inv.GetLocation());
        }

        foreach (var attr in root.DescendantNodes().OfType<AttributeSyntax>())
        {
            if (!IsClauseAttribute(attr)) continue;
            yield return new ClauseSite(FromAttribute(attr), "[" + attr.Name + "]", SymbolOf(attr), attr.ToString(), attr.GetLocation());
        }
    }

    /// <summary><c>Type.Member</c> (or <c>Type</c>) around a node.</summary>
    public static string SymbolOf(SyntaxNode node)
    {
        string? member = null;
        for (var n = node.Parent; n is not null; n = n.Parent)
        {
            switch (n)
            {
                case MethodDeclarationSyntax m when member is null: member = m.Identifier.ValueText; break;
                case ConstructorDeclarationSyntax c when member is null: member = c.Identifier.ValueText; break;
                case PropertyDeclarationSyntax p when member is null: member = p.Identifier.ValueText; break;
                case BaseTypeDeclarationSyntax t:
                    return member is null ? t.Identifier.ValueText : t.Identifier.ValueText + "." + member;
            }
        }
        return member ?? string.Empty;
    }
}
