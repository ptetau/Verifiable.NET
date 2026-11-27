using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Verifiable.Analyzers;

/// <summary>
/// Reports configurable spec-ergonomics nudges inside verified scopes:
/// <list type="bullet">
/// <item><description><c>VER301</c>: overly dense <c>Spec.Requires</c> / <c>Spec.Ensures</c> clauses (&gt;N top-level <c>&amp;&amp;</c> conjuncts or nested ternaries).</description></item>
/// <item><description><c>VER302</c>: missing or blank <c>because</c> explanation on <c>[Verifiable]</c> contracts.</description></item>
/// </list>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SpecQualityAnalyzer : DiagnosticAnalyzer
{
    internal const int DefaultMaxSpecClauses = 3;

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        [
            VerifiableDescriptors.SpecTooComplex,
            VerifiableDescriptors.MissingSpecExplanation
        ];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(compilationCtx =>
        {
            var globalOptions = compilationCtx.Options.AnalyzerConfigOptionsProvider.GlobalOptions;
            var globalMaxClauses = DefaultMaxSpecClauses;
            if (globalOptions.TryGetValue("build_property.VerifiableMaxSpecClauses", out var msbuildMax) &&
                int.TryParse(msbuildMax, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedGlobal) &&
                parsedGlobal >= 1)
            {
                globalMaxClauses = parsedGlobal;
            }

            compilationCtx.RegisterOperationAction(opCtx =>
            {
                var inv = (IInvocationOperation)opCtx.Operation;
                var target = inv.TargetMethod;
                if (target.ContainingType?.ToDisplayString() != VerifiableSymbolHelpers.SpecTypeName)
                {
                    return;
                }

                var methodName = target.Name;
                if (methodName is not ("Requires" or "RequiresWhen" or "Ensures" or "EnsuresWhen"))
                {
                    return;
                }

                var owner = opCtx.ContainingSymbol;
                if (VerifiableSymbolHelpers.IsGeneratedByVerifiableGenerators(owner) ||
                    VerifiableSymbolHelpers.IsSynthesizedRecordMember(owner))
                {
                    return;
                }

                var scope = VerifiableSymbolHelpers.GetScope(owner);
                if (scope == VerificationScope.None)
                {
                    return;
                }

                var isWhen = methodName is "RequiresWhen" or "EnsuresWhen";
                var predicateOrdinal = isWhen ? 1 : 0;
                var becauseOrdinal = isWhen ? 2 : 1;

                var predicateArg = FindArgumentByOrdinal(inv, predicateOrdinal);
                if (predicateArg is null)
                {
                    return;
                }

                var maxClauses = globalMaxClauses;
                var tree = inv.Syntax.SyntaxTree;
                if (tree is not null &&
                    opCtx.Options.AnalyzerConfigOptionsProvider.GetOptions(tree)
                        .TryGetValue("verifiable.max_spec_clauses", out var treeMax) &&
                    int.TryParse(treeMax, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedTree) &&
                    parsedTree >= 1)
                {
                    maxClauses = parsedTree;
                }

                var loc = inv.Syntax.GetLocation();
                var bodyOp = ExtractPredicateBody(predicateArg.Value);
                var conjunctCount = CountTopLevelConjuncts(bodyOp);
                var hasNestedTernary = HasNestedTernary(bodyOp);

                if (conjunctCount > maxClauses || hasNestedTernary)
                {
                    var isSplittable = conjunctCount > maxClauses;
                    var intro = isSplittable && hasNestedTernary
                        ? $"This `Spec.{methodName}` combines {conjunctCount} `&&` clauses (limit: {maxClauses}) and nested ternary (`? :`) branches in one call."
                        : isSplittable
                            ? $"This `Spec.{methodName}` packs {conjunctCount} top-level `&&` clauses into a single call (threshold: {maxClauses})."
                            : $"This `Spec.{methodName}` uses nested ternary (`? :`) conditions inside a single contract call.";

                    var fixHint = isSplittable
                        ? $"Split each conjunct into its own `Spec.{methodName}(..., \"why\")` call (or use `Spec.EnsuresWhen` / `Spec.Implies` for conditional branches)."
                        : "Replace nested ternaries with separate `Spec.EnsuresWhen(...)` / `Spec.RequiresWhen(...)` calls or `Spec.Implies(cond, result)`.";

                    var summary = isSplittable
                        ? $"Spec.{methodName} has {conjunctCount} top-level '&&' clauses (max {maxClauses}); split into separate Spec calls."
                        : $"Spec.{methodName} contains nested ternary expressions; split with Spec.EnsuresWhen or Spec.Implies.";

                    var elm = ElmDiagnosticFormatter.Format(
                        "COMPLEX CONTRACT",
                        loc,
                        intro,
                        spirit: "Splitting complex contracts into small, focused rules lets Z3 pinpoint the exact broken requirement and surface a dedicated plain-English explanation.",
                        fixHint: fixHint,
                        summary: summary);

                    var props = isSplittable
                        ? ImmutableDictionary<string, string?>.Empty.Add("FixKind", "VER301_SplitConjuncts")
                        : ImmutableDictionary<string, string?>.Empty;

                    opCtx.ReportDiagnostic(Diagnostic.Create(
                        VerifiableDescriptors.SpecTooComplex,
                        loc,
                        properties: props,
                        elm));
                }

                // VER302: missing or blank `because` on [Verifiable] Spec.Requires/RequiresWhen/Ensures/EnsuresWhen
                if (scope == VerificationScope.Verifiable)
                {
                    var becauseArg = FindArgumentByOrdinal(inv, becauseOrdinal);
                    if (IsMissingOrBlankBecause(becauseArg))
                    {
                        var elm = ElmDiagnosticFormatter.Format(
                            "MISSING CONTRACT EXPLANATION",
                            loc,
                            $"This `Spec.{methodName}` call omits a plain-English `because` explanation.",
                            spirit: "The `because` string explains the domain intent behind a contract and appears directly in Elm `VER102` diagnostics and `<verifiable-counterexample>` JSON.",
                            fixHint: $"Add a trailing explanation string, for example: `Spec.{methodName}(..., \"Explain why this rule must hold.\");`.",
                            summary: $"Spec.{methodName} is missing a plain-English 'because' explanation.");

                        opCtx.ReportDiagnostic(Diagnostic.Create(
                            VerifiableDescriptors.MissingSpecExplanation,
                            loc,
                            elm));
                    }
                }
            }, OperationKind.Invocation);
        });
    }

    private static IArgumentOperation? FindArgumentByOrdinal(IInvocationOperation inv, int ordinal)
    {
        foreach (var arg in inv.Arguments)
        {
            if (arg.Parameter?.Ordinal == ordinal)
            {
                return arg;
            }
        }
        return ordinal < inv.Arguments.Length ? inv.Arguments[ordinal] : null;
    }

    private static bool IsMissingOrBlankBecause(IArgumentOperation? arg)
    {
        if (arg is null || arg.ArgumentKind == ArgumentKind.DefaultValue)
        {
            return true;
        }

        var val = Unwrap(arg.Value);
        if (val.ConstantValue.HasValue)
        {
            return val.ConstantValue.Value is not string s || string.IsNullOrWhiteSpace(s);
        }

        return false;
    }

    private static IOperation ExtractPredicateBody(IOperation op)
    {
        var cur = Unwrap(op);
        if (cur is IDelegateCreationOperation del)
        {
            cur = Unwrap(del.Target);
        }

        if (cur is IAnonymousFunctionOperation anon &&
            anon.Body is { Operations.Length: 1 } block &&
            block.Operations[0] is IReturnOperation { ReturnedValue: { } retExpr })
        {
            return Unwrap(retExpr);
        }

        return cur;
    }

    private static IOperation Unwrap(IOperation op)
    {
        var cur = op;
        while (cur is IConversionOperation or IParenthesizedOperation)
        {
            cur = cur is IConversionOperation c ? c.Operand : ((IParenthesizedOperation)cur).Operand;
        }
        return cur;
    }

    private static int CountTopLevelConjuncts(IOperation op)
    {
        var cur = Unwrap(op);
        if (cur is IBinaryOperation { OperatorKind: BinaryOperatorKind.ConditionalAnd } bin)
        {
            return CountTopLevelConjuncts(bin.LeftOperand) + CountTopLevelConjuncts(bin.RightOperand);
        }
        return 1;
    }

    private static bool HasNestedTernary(IOperation op)
    {
        foreach (var cond in op.DescendantsAndSelf().OfType<IConditionalOperation>())
        {
            if (cond.Descendants().OfType<IConditionalOperation>().Any())
            {
                return true;
            }
        }
        return false;
    }
}
