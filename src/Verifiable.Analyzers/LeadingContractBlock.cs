using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Verifiable.Shared;

/// <summary>Where a <c>Spec.*</c> call sits relative to its member's leading contract block.</summary>
internal enum ContractPlacement
{
    /// <summary>In the leading contract block, or the whole expression body.</summary>
    Leading,

    /// <summary>A top-level statement, but after a statement that is not a <c>Spec.*</c> call.</summary>
    AfterStatement,

    /// <summary>Nested inside an <c>if</c>, loop, <c>switch</c>, <c>try</c> or other statement.</summary>
    Nested,

    /// <summary>Inside a lambda or anonymous method.</summary>
    InLambda,

    /// <summary>Inside a local function.</summary>
    InLocalFunction,

    /// <summary>Somewhere that has no contract block, such as a top-level program or a field initializer.</summary>
    NoContractBlock,
}

/// <summary>
/// The leading contract block of a method, constructor, operator or accessor: its top-level statements before
/// the first statement that is not a <c>Spec.*</c> call. A <c>Spec.Requires</c> / <c>Spec.RequiresWhen</c> is a
/// precondition only if it is in this block. <c>Spec.Ensures</c> and <c>Spec.Requires</c> may come in any order
/// inside it. An expression-bodied member whose body is one <c>Spec.*</c> call counts as a block of one.
/// </summary>
/// <remarks>
/// This one file is compiled into both Verifiable.Analyzers and Verifiable.Generators, so the prover, the
/// <c>[InboundPort]</c> runtime gate and the cross-assembly contract export all agree on which preconditions count.
/// Callers pass the <c>isSpecCall</c> test: a semantic check where a model is at hand, a syntactic one otherwise.
/// </remarks>
internal static class LeadingContractBlock
{
    /// <summary>The <c>Spec.*</c> calls in <paramref name="member"/>'s leading contract block, in source order.</summary>
    public static List<InvocationExpressionSyntax> GetLeadingSpecCalls(
        SyntaxNode member,
        Func<InvocationExpressionSyntax, bool> isSpecCall)
    {
        var result = new List<InvocationExpressionSyntax>();
        var (body, expressionBody) = GetBodies(member);

        if (expressionBody is InvocationExpressionSyntax arrowCall && isSpecCall(arrowCall))
        {
            result.Add(arrowCall);
        }

        if (body is not null)
        {
            foreach (var stmt in body.Statements)
            {
                if (!IsSpecStatement(stmt, isSpecCall, out var call))
                {
                    break;
                }
                result.Add(call!);
            }
        }

        return result;
    }

    /// <summary>
    /// Says where <paramref name="call"/> sits. For <see cref="ContractPlacement.AfterStatement"/> and
    /// <see cref="ContractPlacement.Nested"/>, <paramref name="culprit"/> is the top-level statement that ends the
    /// block or contains the call.
    /// </summary>
    public static ContractPlacement Classify(
        InvocationExpressionSyntax call,
        Func<InvocationExpressionSyntax, bool> isSpecCall,
        out StatementSyntax? culprit)
    {
        culprit = null;
        SyntaxNode? owner = null;
        for (var node = call.Parent; node is not null; node = node.Parent)
        {
            if (node is AnonymousFunctionExpressionSyntax)
            {
                return ContractPlacement.InLambda;
            }

            if (node is LocalFunctionStatementSyntax)
            {
                return ContractPlacement.InLocalFunction;
            }

            if (node is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax)
            {
                owner = node;
                break;
            }

            if (node is MemberDeclarationSyntax or GlobalStatementSyntax)
            {
                return ContractPlacement.NoContractBlock;
            }
        }

        if (owner is null)
        {
            return ContractPlacement.NoContractBlock;
        }

        var (body, expressionBody) = GetBodies(owner);
        if (expressionBody == call)
        {
            return ContractPlacement.Leading;
        }

        if (body is null)
        {
            return ContractPlacement.Nested;
        }

        StatementSyntax? topLevel = null;
        foreach (var stmt in body.Statements)
        {
            if (stmt.Span.Contains(call.Span))
            {
                topLevel = stmt;
                break;
            }
        }

        if (topLevel is null)
        {
            return ContractPlacement.Nested;
        }

        if (topLevel is not ExpressionStatementSyntax { Expression: var expr } || expr != call)
        {
            culprit = topLevel;
            return ContractPlacement.Nested;
        }

        foreach (var stmt in body.Statements)
        {
            if (stmt == topLevel)
            {
                return ContractPlacement.Leading;
            }

            if (!IsSpecStatement(stmt, isSpecCall, out _))
            {
                culprit = stmt;
                return ContractPlacement.AfterStatement;
            }
        }

        return ContractPlacement.Leading;
    }

    private static bool IsSpecStatement(
        StatementSyntax stmt,
        Func<InvocationExpressionSyntax, bool> isSpecCall,
        out InvocationExpressionSyntax? call)
    {
        if (stmt is ExpressionStatementSyntax { Expression: InvocationExpressionSyntax inv } && isSpecCall(inv))
        {
            call = inv;
            return true;
        }

        call = null;
        return false;
    }

    private static (BlockSyntax? Body, ExpressionSyntax? ExpressionBody) GetBodies(SyntaxNode member) => member switch
    {
        BaseMethodDeclarationSyntax m => (m.Body, m.ExpressionBody?.Expression),
        AccessorDeclarationSyntax a => (a.Body, a.ExpressionBody?.Expression),
        LocalFunctionStatementSyntax f => (f.Body, f.ExpressionBody?.Expression),
        _ => (null, null),
    };
}
