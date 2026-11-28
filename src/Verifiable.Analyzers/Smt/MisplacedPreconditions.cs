using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;
using Verifiable.Shared;

namespace Verifiable.Analyzers.Smt;

/// <summary>
/// Reports <c>VER101</c> for a <c>Spec.Requires</c> / <c>Spec.RequiresWhen</c> that is not in its method's leading
/// contract block. The lowerer drops such a call (it is neither assumed at entry nor required of callers), so
/// without this error it would silently do nothing.
/// </summary>
internal static class MisplacedPreconditions
{
    public static void Report(OperationBlockAnalysisContext ctx, IMethodSymbol method)
    {
        var seen = new HashSet<TextSpan>();
        foreach (var block in ctx.OperationBlocks)
        {
            foreach (var op in block.DescendantsAndSelf())
            {
                if (op is not IInvocationOperation inv ||
                    !(VerifiableSymbolHelpers.IsSpecCall(inv.TargetMethod, "Requires") ||
                      VerifiableSymbolHelpers.IsSpecCall(inv.TargetMethod, "RequiresWhen")) ||
                    inv.Syntax is not InvocationExpressionSyntax call ||
                    inv.SemanticModel is not { } model ||
                    !seen.Add(call.Span))
                {
                    continue;
                }

                var placement = LeadingContractBlock.Classify(
                    call, c => VerifiableSymbolHelpers.BindsToSpec(model, c), out var culprit);
                if (placement == ContractPlacement.Leading)
                {
                    continue;
                }

                ctx.ReportDiagnostic(Diagnostic.Create(
                    VerifiableDescriptors.InvalidContractExpression,
                    call.GetLocation(),
                    Format(method, inv.TargetMethod.Name, call, placement, culprit)));
            }
        }
    }

    private static string Format(
        IMethodSymbol method,
        string specName,
        InvocationExpressionSyntax call,
        ContractPlacement placement,
        StatementSyntax? culprit)
    {
        var display = $"{method.ContainingType?.Name}.{method.Name}";
        var where = placement switch
        {
            ContractPlacement.AfterStatement when culprit is not null =>
                $"It comes after `{FirstLine(culprit)}` (line {Line(culprit)}), which is not a `Spec.*` call, so it is not part of the contract block at the top of the method.",
            ContractPlacement.Nested when culprit is not null =>
                $"It is inside {Describe(culprit, call)} instead of at the top of the method.",
            ContractPlacement.InLambda =>
                "It is inside a lambda. A lambda has no contract block, so nothing checks this call.",
            ContractPlacement.InLocalFunction =>
                "It is inside a local function. Contracts on local functions are not read, so nothing checks this call.",
            _ => "It is not at the top of a method body.",
        };

        var nestedFunction = placement is ContractPlacement.InLambda or ContractPlacement.InLocalFunction;
        var fixHint = nestedFunction
            ? $"Fix: If it is a rule for the inputs of `{display}`, move it to the top of `{method.Name}`. Otherwise make the lambda or local function a `[Verifiable]` method and put the `Spec.{specName}` at the top of that method."
            : $"Fix: Move it to the top of `{method.Name}`, next to the other `Spec.*` calls. If it should only hold in some cases, write `Spec.RequiresWhen(condition, requirement, \"why\")` at the top instead, or have callers check the condition with an `if` before they call.";
        var summary = nestedFunction
            ? $"Spec.{specName} inside a {(placement == ContractPlacement.InLambda ? "lambda" : "local function")} in {display} is ignored; move it to the top of {method.Name} or into a [Verifiable] method's contract block."
            : $"Spec.{specName} in {display} is not at the top of the method, so it is ignored; move it into the leading contract block or use Spec.RequiresWhen(condition, requirement, because).";

        return ElmDiagnosticFormatter.Format(
            "UNSUPPORTED CONTRACT",
            call.GetLocation(),
            $"This `Spec.{specName}` in `{display}` is not a precondition. {where}",
            spirit: "A precondition is something every caller must make true before the call. Only the `Spec.*` calls at the top of a method can mean that. If a `Spec.Requires` inside a branch counted, every caller would have to satisfy it, and assuming it on every path could hide bugs on the other paths.",
            explanation: "Only the `Spec.Requires` / `Spec.RequiresWhen` calls in the leading contract block are preconditions. That block is the top-level statements before the first statement that is not a `Spec.*` call, in any order. This call is neither assumed when the method is proved nor required of callers.",
            fixHint: fixHint,
            summary: summary);
    }

    private static string Describe(StatementSyntax culprit, InvocationExpressionSyntax call) => culprit switch
    {
        IfStatementSyntax ifs when ifs.Else is not null && ifs.Else.Span.Contains(call.Span) => "the `else` branch of an `if` statement",
        IfStatementSyntax => "an `if` statement",
        ForStatementSyntax or CommonForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax => "a loop",
        SwitchStatementSyntax => "a `switch` statement",
        TryStatementSyntax => "a `try` statement",
        UsingStatementSyntax => "a `using` statement",
        LockStatementSyntax => "a `lock` statement",
        CheckedStatementSyntax => "a `checked` or `unchecked` block",
        BlockSyntax => "a nested `{ }` block",
        _ => "another statement",
    };

    private static string FirstLine(SyntaxNode node)
    {
        var text = node.ToString().Trim();
        var newline = text.IndexOf('\n');
        return newline < 0 ? text : text.Substring(0, newline).TrimEnd() + " ...";
    }

    private static int Line(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
}
