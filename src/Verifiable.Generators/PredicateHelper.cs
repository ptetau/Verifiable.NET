using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Verifiable.Generators;

internal static class PredicateHelper
{
    public sealed record RewrittenPredicate(
        bool IsValid,
        string? ErrorMessage,
        string ValueExpression,
        string NormalizedOriginalExpression,
        int? UpperBoundExclusiveForZeroBasedInt);

    public static RewrittenPredicate RewriteRefinementPredicate(string? predicate, ITypeSymbol underlyingType)
    {
        if (string.IsNullOrWhiteSpace(predicate))
        {
            return new RewrittenPredicate(false, "Predicate cannot be empty.", string.Empty, string.Empty, null);
        }

        var expr = SyntaxFactory.ParseExpression(predicate!);
        var firstError = expr.GetDiagnostics().FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error);
        if (firstError is not null || expr.FullSpan.Length != predicate!.Length)
        {
            var errMsg = firstError?.GetMessage() ?? "Unexpected trailing tokens in predicate.";
            return new RewrittenPredicate(false, errMsg, string.Empty, string.Empty, null);
        }

        var hasNullLiteral = expr.DescendantNodesAndSelf().Any(n => n.IsKind(SyntaxKind.NullLiteralExpression));
        var valueRewriter = new ValuePredicateRewriter("value");
        var rewrittenToValue = valueRewriter.Visit(expr)!.ToFullString().Trim();

        var originalRewriter = new ValuePredicateRewriter("Value");
        var rewrittenOriginal = originalRewriter.Visit(expr)!.ToFullString().Trim();

        if (underlyingType.IsReferenceType && !hasNullLiteral)
        {
            rewrittenToValue = $"value != null && ({rewrittenToValue})";
            rewrittenOriginal = $"Value != null && ({rewrittenOriginal})";
        }

        int? upperBound = null;
        if (underlyingType.SpecialType == SpecialType.System_Int32)
        {
            upperBound = TryExtractZeroBasedUpperBound(expr);
        }

        return new RewrittenPredicate(true, null, rewrittenToValue, rewrittenOriginal, upperBound);
    }

    public static bool TryValidateExpression(string expression, out ExpressionSyntax? parsed, out string? error)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            parsed = null;
            error = "Expression cannot be empty.";
            return false;
        }

        var syntax = SyntaxFactory.ParseExpression(expression);
        var diag = syntax.GetDiagnostics().FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error);
        if (diag is not null || syntax.FullSpan.Length != expression.Length)
        {
            parsed = null;
            error = diag?.GetMessage() ?? "Syntax error in expression.";
            return false;
        }

        parsed = syntax;
        error = null;
        return true;
    }

    public static string QualifyMembers(ExpressionSyntax expr, string targetVariable, ISet<string> memberNames)
    {
        var rewriter = new MemberQualifierRewriter(targetVariable, memberNames);
        return rewriter.Visit(expr)!.ToFullString().Trim();
    }

    private static int? TryExtractZeroBasedUpperBound(ExpressionSyntax expr)
    {
        int? minInclusive = null;
        int? maxExclusive = null;

        void InspectClause(ExpressionSyntax node)
        {
            node = UnwrapParens(node);
            if (node is BinaryExpressionSyntax bin)
            {
                if (bin.IsKind(SyntaxKind.LogicalAndExpression))
                {
                    InspectClause(bin.Left);
                    InspectClause(bin.Right);
                    return;
                }

                if (TryGetIdentifierBound(bin.Left, bin.Right, bin.Kind(), out var isLower, out var inclusiveVal, out var exclusiveVal))
                {
                    if (isLower)
                    {
                        minInclusive = minInclusive is null ? inclusiveVal : Math.Max(minInclusive.Value, inclusiveVal);
                    }
                    else
                    {
                        maxExclusive = maxExclusive is null ? exclusiveVal : Math.Min(maxExclusive.Value, exclusiveVal);
                    }
                }
            }
            else if (node is IsPatternExpressionSyntax isPat &&
                     UnwrapParens(isPat.Expression) is IdentifierNameSyntax id &&
                     id.Identifier.ValueText == "Value")
            {
                InspectPattern(isPat.Pattern, ref minInclusive, ref maxExclusive);
            }
        }

        InspectClause(expr);
        if (minInclusive is >= 0 && maxExclusive is > 0 && maxExclusive > minInclusive)
        {
            return maxExclusive;
        }

        return null;
    }

    private static void InspectPattern(PatternSyntax pattern, ref int? minInclusive, ref int? maxExclusive)
    {
        switch (pattern)
        {
            case ParenthesizedPatternSyntax paren:
                InspectPattern(paren.Pattern, ref minInclusive, ref maxExclusive);
                break;
            case BinaryPatternSyntax bin when bin.IsKind(SyntaxKind.AndPattern):
                InspectPattern(bin.Left, ref minInclusive, ref maxExclusive);
                InspectPattern(bin.Right, ref minInclusive, ref maxExclusive);
                break;
            case RelationalPatternSyntax rel when TryEvalIntLiteral(rel.Expression, out var v):
                switch (rel.OperatorToken.Kind())
                {
                    case SyntaxKind.GreaterThanEqualsToken:
                        minInclusive = minInclusive is null ? v : Math.Max(minInclusive.Value, v);
                        break;
                    case SyntaxKind.GreaterThanToken when v < int.MaxValue:
                        minInclusive = minInclusive is null ? v + 1 : Math.Max(minInclusive.Value, v + 1);
                        break;
                    case SyntaxKind.LessThanToken:
                        maxExclusive = maxExclusive is null ? v : Math.Min(maxExclusive.Value, v);
                        break;
                    case SyntaxKind.LessThanEqualsToken when v < int.MaxValue:
                        maxExclusive = maxExclusive is null ? v + 1 : Math.Min(maxExclusive.Value, v + 1);
                        break;
                }
                break;
        }
    }

    private static bool TryGetIdentifierBound(
        ExpressionSyntax left,
        ExpressionSyntax right,
        SyntaxKind kind,
        out bool isLower,
        out int inclusiveLower,
        out int exclusiveUpper)
    {
        isLower = false;
        inclusiveLower = 0;
        exclusiveUpper = 0;

        left = UnwrapParens(left);
        right = UnwrapParens(right);

        if (left is IdentifierNameSyntax idLeft && idLeft.Identifier.ValueText == "Value" && TryEvalIntLiteral(right, out var rVal))
        {
            switch (kind)
            {
                case SyntaxKind.GreaterThanOrEqualExpression:
                    isLower = true; inclusiveLower = rVal; return true;
                case SyntaxKind.GreaterThanExpression when rVal < int.MaxValue:
                    isLower = true; inclusiveLower = rVal + 1; return true;
                case SyntaxKind.LessThanExpression:
                    isLower = false; exclusiveUpper = rVal; return true;
                case SyntaxKind.LessThanOrEqualExpression when rVal < int.MaxValue:
                    isLower = false; exclusiveUpper = rVal + 1; return true;
            }
        }
        else if (right is IdentifierNameSyntax idRight && idRight.Identifier.ValueText == "Value" && TryEvalIntLiteral(left, out var lVal))
        {
            switch (kind)
            {
                case SyntaxKind.LessThanOrEqualExpression:
                    isLower = true; inclusiveLower = lVal; return true;
                case SyntaxKind.LessThanExpression when lVal < int.MaxValue:
                    isLower = true; inclusiveLower = lVal + 1; return true;
                case SyntaxKind.GreaterThanExpression:
                    isLower = false; exclusiveUpper = lVal; return true;
                case SyntaxKind.GreaterThanOrEqualExpression when lVal < int.MaxValue:
                    isLower = false; exclusiveUpper = lVal + 1; return true;
            }
        }

        return false;
    }

    private static ExpressionSyntax UnwrapParens(ExpressionSyntax expr)
    {
        while (expr is ParenthesizedExpressionSyntax p)
        {
            expr = p.Expression;
        }
        return expr;
    }

    private static bool TryEvalIntLiteral(ExpressionSyntax expr, out int value)
    {
        expr = UnwrapParens(expr);
        if (expr is LiteralExpressionSyntax lit && lit.Token.Value is int i)
        {
            value = i;
            return true;
        }

        if (expr is PrefixUnaryExpressionSyntax pre && pre.IsKind(SyntaxKind.UnaryMinusExpression) &&
            pre.Operand is LiteralExpressionSyntax inner && inner.Token.Value is int iv && iv != int.MinValue)
        {
            value = -iv;
            return true;
        }

        value = 0;
        return false;
    }

    private sealed class ValuePredicateRewriter(string valueName) : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            if (IsDetachedIdentifier(node))
            {
                if (node.Identifier.ValueText == "Value")
                {
                    return SyntaxFactory.IdentifierName(valueName).WithTriviaFrom(node);
                }

                if (node.Identifier.ValueText == "Length")
                {
                    return SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName(valueName),
                        SyntaxFactory.IdentifierName("Length")).WithTriviaFrom(node);
                }
            }

            return base.VisitIdentifierName(node);
        }
    }

    private sealed class MemberQualifierRewriter(string targetVariable, ISet<string> memberNames) : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitThisExpression(ThisExpressionSyntax node) =>
            SyntaxFactory.IdentifierName(targetVariable).WithTriviaFrom(node);

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            if (IsDetachedIdentifier(node) && memberNames.Contains(node.Identifier.ValueText))
            {
                return SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName(targetVariable),
                    SyntaxFactory.IdentifierName(node.Identifier.ValueText)).WithTriviaFrom(node);
            }

            return base.VisitIdentifierName(node);
        }
    }

    private static bool IsDetachedIdentifier(IdentifierNameSyntax node)
    {
        if (node.Parent is MemberAccessExpressionSyntax ma && ma.Name == node)
        {
            return false;
        }

        if (node.Parent is MemberBindingExpressionSyntax mb && mb.Name == node)
        {
            return false;
        }

        if (node.Parent is NameColonSyntax nc && nc.Name == node)
        {
            return false;
        }

        if (node.Parent is NameEqualsSyntax ne && ne.Name == node)
        {
            return false;
        }

        return true;
    }
}
