using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Verifiable.Analyzers.Smt;

// Reading VERIFIABLE_AGENT environment variable for deterministic agent diagnostics
#pragma warning disable RS1035

/// <summary>
/// Verifies <c>Spec.Requires</c>, <c>Spec.Ensures</c>, <c>[Invariant]</c>, <c>Spec.LoopInvariant</c>, <c>Spec.Decreases</c>,
/// <c>[TypeAlias&lt;T&gt;]</c>, and <c>[CheckedArithmetic]</c> contracts inside <c>[Verifiable]</c> methods using CFG-to-SSA lowering and out-of-process Z3.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class VerifiableSmtAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        [
            VerifiableDescriptors.InvalidContractExpression,
            VerifiableDescriptors.ProofObligationFailed,
            VerifiableDescriptors.SmtSolverUnknown,
            VerifiableDescriptors.Z3SolverUnavailable
        ];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(compilationCtx =>
        {
            var globalOptions = compilationCtx.Options.AnalyzerConfigOptionsProvider.GlobalOptions;
            globalOptions.TryGetValue("build_property.VerifiableZ3Path", out var msbuildZ3Path);
            globalOptions.TryGetValue("build_property.VerifiableAgent", out var msbuildAgent);
            globalOptions.TryGetValue("build_property.VerifiableClosedEnums", out var enumOption);
            var closedEnums = bool.TryParse(enumOption, out var enabled) && enabled;

            var envAgent = Environment.GetEnvironmentVariable("VERIFIABLE_AGENT");
            var isAgentMode =
                envAgent == "1" || string.Equals(envAgent, "true", StringComparison.OrdinalIgnoreCase) ||
                msbuildAgent == "1" || string.Equals(msbuildAgent, "true", StringComparison.OrdinalIgnoreCase);

            var reportedUnavailable = 0;

            compilationCtx.RegisterOperationBlockAction(opCtx =>
            {
                if (opCtx.OwningSymbol is not IMethodSymbol method) return;
                if (VerifiableSymbolHelpers.IsSynthesizedRecordMember(method) ||
                    VerifiableSymbolHelpers.IsGeneratedByVerifiableGenerators(method) ||
                    VerifiableSymbolHelpers.HasAttribute(method, VerifiableSymbolHelpers.PureAxiomAttr))
                {
                    return;
                }

                if (VerifiableSymbolHelpers.GetScope(method) != VerificationScope.Verifiable)
                {
                    return;
                }

                if (!MethodNeedsSmtCheck(opCtx.Compilation, method, opCtx.OperationBlocks))
                {
                    return;
                }

                var sourceTree = opCtx.OperationBlocks.FirstOrDefault()?.Syntax.SyntaxTree;
                var lowerer = new CfgSsaLowerer(opCtx.Compilation, method, closedEnums: closedEnums);

                var allObligations = new List<ProofObligation>();
                var allErrors = new List<(string Contract, Location Location, string Error)>();

                foreach (var block in opCtx.OperationBlocks)
                {
                    if (block is IBlockOperation or IMethodBodyOperation or IConstructorBodyOperation)
                    {
                        var cfg = opCtx.GetControlFlowGraph(block);
                        var outcome = lowerer.Lower(cfg, opCtx.OperationBlocks);
                        allObligations.AddRange(outcome.Obligations);
                        allErrors.AddRange(outcome.Errors);
                    }
                }

                foreach (var err in allErrors)
                {
                    var elmErr = ElmDiagnosticFormatter.Format(
                        "UNSUPPORTED CONTRACT",
                        err.Location,
                        $"Could not translate contract `{err.Contract}` in `{method.ContainingType?.Name}.{method.Name}` into an SMT formula.",
                        explanation: $"Reason: {err.Error}",
                        fixHint: "Rewrite the predicate using supported deterministic C# boolean, arithmetic, string, or immutable collection expressions.");
                    opCtx.ReportDiagnostic(Diagnostic.Create(
                        VerifiableDescriptors.InvalidContractExpression,
                        err.Location,
                        elmErr));
                }

                foreach (var ob in allObligations)
                {
                    var script = BuildQueryScript(ob);
                    var result = Z3SolverRunner.Execute(script, msbuildZ3Path, sourceTree?.FilePath);

                    if (result.Status == SmtStatus.SolverUnavailable)
                    {
                        if (Interlocked.Exchange(ref reportedUnavailable, 1) == 0)
                        {
                            var z3Loc = method.Locations.FirstOrDefault() ?? ob.Location;
                            var elmMissing = ElmDiagnosticFormatter.Format(
                                "Z3 SOLVER NOT FOUND",
                                z3Loc,
                                "The Z3 SMT solver executable could not be found, so compile-time proof checks were skipped.",
                                explanation: "Checked `VERIFIABLE_Z3_PATH`, `.tools/z3`, `~/.verifiable/z3`, and `PATH`.",
                                fixHint: "Install the `Verifiable.Z3` NuGet package (included with `Verifiable.NET`), pass `-p:VerifiableZ3Path=...`, set `VERIFIABLE_Z3_PATH`, or install `z3` on `PATH`.");
                            opCtx.ReportDiagnostic(Diagnostic.Create(
                                VerifiableDescriptors.Z3SolverUnavailable,
                                z3Loc,
                                elmMissing));
                        }
                        return;
                    }

                    if (result.Status == SmtStatus.Sat)
                    {
                        var ceSummary = FormatCounterexampleSummary(ob, result.Model);
                        var fixHint = GetFixHint(ob.Kind);
                        var agentPayload = isAgentMode
                            ? BuildAgentJsonBlock(method, ob, result, fixHint)
                            : null;
                        var elmFail = ElmDiagnosticFormatter.Format(
                            "PROOF FAILED",
                            ob.Location,
                            $"Z3 found inputs that break a {FormatKindLabel(ob.Kind)} in `{method.ContainingType?.Name}.{method.Name}`.",
                            spirit: ob.Reason,
                            contract: $"{ob.Kind}: {ob.ContractText}",
                            counterexample: ceSummary,
                            fixHint: fixHint,
                            agentPayload: agentPayload);

                        opCtx.ReportDiagnostic(Diagnostic.Create(
                            VerifiableDescriptors.ProofObligationFailed,
                            ob.Location,
                            $"{ob.Kind} of {method.ContainingType?.Name}.{method.Name} fails: " +
                            OneLine(ob.Reason ?? ob.ContractText) + "; " + OneLine(ceSummary) +
                            Environment.NewLine + elmFail));
                    }
                    else if (result.Status is SmtStatus.Unknown or SmtStatus.Error)
                    {
                        var elmUnknown = ElmDiagnosticFormatter.Format(
                            "SOLVER COULD NOT DECIDE",
                            ob.Location,
                            $"Z3 returned `{result.Status}` while checking `{ob.ContractText}` in `{method.ContainingType?.Name}.{method.Name}`.",
                            spirit: ob.Reason,
                            explanation: string.IsNullOrWhiteSpace(result.ErrorMessage) ? null : result.ErrorMessage,
                            fixHint: "Simplify the contract, add `Spec.LoopInvariant` or `Spec.Decreases` annotations, or factor complex steps into helper lemmas.");
                        opCtx.ReportDiagnostic(Diagnostic.Create(
                            VerifiableDescriptors.SmtSolverUnknown,
                            ob.Location,
                            elmUnknown));
                    }
                }
            });
        });
    }

    internal static string BuildQueryScript(ProofObligation ob)
    {
        var sb = new StringBuilder(ob.ScriptPreamble.Length + 256);
        sb.AppendLine(ob.ScriptPreamble);
        if (!string.IsNullOrEmpty(ob.PathCondition) && ob.PathCondition != "true")
        {
            sb.Append("(assert ").Append(ob.PathCondition).AppendLine(")");
        }
        sb.Append("(assert (not ").Append(ob.GoalTerm).AppendLine("))");
        return sb.ToString();
    }

    private static bool MethodNeedsSmtCheck(Compilation compilation, IMethodSymbol method, ImmutableArray<IOperation> blocks)
    {
        if (VerifiableSymbolHelpers.HasAttribute(method, VerifiableSymbolHelpers.CheckedArithmeticAttr) ||
            (method.ContainingType is not null && VerifiableSymbolHelpers.HasAttribute(method.ContainingType, VerifiableSymbolHelpers.CheckedArithmeticAttr)) ||
            (method.ContainingAssembly is not null && VerifiableSymbolHelpers.HasAttribute(method.ContainingAssembly, VerifiableSymbolHelpers.CheckedArithmeticAttr)) ||
            SmtTypeSystem.HasAnyInvariants(method.ReturnType) ||
            IsResultWithInvariantPayload(method.ReturnType))
        {
            return true;
        }

        foreach (var b in blocks)
        {
            foreach (var op in b.DescendantsAndSelf())
            {
                if (op is IInvocationOperation cal && (CalendarArithmetic.CalendarType(cal.TargetMethod.ContainingType) is not null ||
                    cal.TargetMethod.ContainingType.SpecialType == SpecialType.System_Decimal && cal.TargetMethod.Name == "Round") ||
                    op is IObjectCreationOperation ctor && CalendarArithmetic.CalendarType(ctor.Type) is not null ||
                    op is IBinaryOperation bin && CalendarArithmetic.CalendarType(bin.Type) is not null ||
                    op is IUnaryOperation unary && CalendarArithmetic.CalendarType(unary.Type) is not null ||
                    op is IPropertyReferenceOperation property && CalendarArithmetic.CalendarType(property.Property.ContainingType) is not null)
                    return true;

                if (op is IInvocationOperation inv)
                {
                    if (RegexModel.IsMatchMethod(inv.TargetMethod))
                        return true;

                    // Instance string searches and Trim require a non-null receiver
                    // even when the method has no explicit specification.
                    if (StringIntrinsics.Handles(inv.TargetMethod) &&
                        (!inv.TargetMethod.IsStatic || inv.TargetMethod.Name == "Join"))
                        return true;

                    if (inv.TargetMethod.ContainingType?.ToDisplayString() == VerifiableSymbolHelpers.SpecTypeName)
                    {
                        return true;
                    }

                    if (VerifiableSymbolHelpers.IsLinqProvider(inv.TargetMethod))
                    {
                        return true;
                    }

                    if (inv.TargetMethod.Name == "From" &&
                        inv.TargetMethod.ContainingType is { } ct &&
                        SmtTypeSystem.TryGetConstrainedUnderlying(ct, out _))
                    {
                        return true;
                    }

                    if (CfgSsaLowerer.MethodHasSpecRequires(compilation, inv.TargetMethod))
                    {
                        return true;
                    }

                    if (inv.TargetMethod.Name == "Add" &&
                        inv.TargetMethod.ContainingType?.OriginalDefinition.ToDisplayString() == "System.Collections.Immutable.ImmutableDictionary<TKey, TValue>")
                    {
                        return true;
                    }
                }

                if (op is IPropertyReferenceOperation pr && pr.Property.IsIndexer)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsResultWithInvariantPayload(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol named &&
            named.OriginalDefinition.ToDisplayString() == "Verifiable.Contracts.Result<T, TError>")
        {
            return SmtTypeSystem.HasAnyInvariants(named.TypeArguments[0]);
        }
        return false;
    }

    private static string FormatCounterexampleSummary(ProofObligation ob, IReadOnlyDictionary<string, string> model)
    {
        var bindings = new List<string>();
        foreach (var (paramName, smtName) in ob.TrackedParameters)
        {
            if (model.TryGetValue(smtName, out var val))
            {
                bindings.Add($"{paramName} = {val}");
            }
        }

        if (bindings.Count == 0)
        {
            foreach (var kv in model.Where(k => !k.Key.StartsWith("ssa_", StringComparison.Ordinal)).Take(4))
            {
                bindings.Add($"{kv.Key} = {kv.Value}");
            }
        }

        return bindings.Count > 0
            ? string.Join("\n    ", bindings)
            : "(solver found a violating assignment)";
    }

    private static string OneLine(string text) =>
        text.Replace("\n    ", ", ").Replace('\r', ' ').Replace('\n', ' ');

    private static string FormatKindLabel(string obligationKind) => obligationKind switch
    {
        "Precondition" => "callee precondition",
        "Postcondition" => "postcondition",
        "TypeInvariant" => "type invariant",
        "Overflow" => "checked arithmetic check",
        "DivisionByZero" => "division-by-zero check",
        "CollectionBounds" => "collection bounds check",
        "LoopInvariantEntry" => "loop invariant on entry",
        "LoopInvariantStep" => "loop invariant preservation",
        "LoopDecreasesEntry" => "loop termination measure on entry",
        "LoopDecreasesStep" => "loop termination decrease check",
        _ => "contract obligation"
    };

    private static string GetFixHint(string obligationKind) => obligationKind switch
    {
        "Precondition" => "Fix: Add a Spec.Requires(...) precondition or an 'if' guard proving the callee precondition holds along this path.",
        "Postcondition" => "Fix: Strengthen Spec.Requires(...) preconditions or correct the return value to satisfy Spec.Ensures(...).",
        "TypeInvariant" => "Fix: Ensure all fields and [Invariant] / IsValid checks of the returned/constructed type hold under all input paths.",
        "Overflow" => "Fix: Add integer bounds via Spec.Requires(...) or [TypeAlias<int>] so arithmetic cannot overflow 32/64-bit bounds.",
        "CollectionBounds" => "Fix: Guard collection access with ContainsKey / index bounds check or strengthen [Invariant] / Spec.Requires.",
        "LoopInvariantEntry" => "Fix: Ensure Spec.LoopInvariant(...) holds immediately before entering the loop.",
        "LoopInvariantStep" => "Fix: Strengthen Spec.LoopInvariant(...) or update the loop body so the invariant is preserved on every iteration.",
        "LoopDecreasesEntry" or "LoopDecreasesStep" => "Fix: Ensure Spec.Decreases(...) stays non-negative and strictly decreases on every iteration.",
        _ => "Fix: Strengthen preconditions or fix the implementation so the contract holds on all paths."
    };

    private static string BuildAgentJsonBlock(
        IMethodSymbol method,
        ProofObligation ob,
        SmtQueryResult result,
        string fixHint)
    {
        var cePairs = new List<string>();
        foreach (var (paramName, smtName) in ob.TrackedParameters)
        {
            if (result.Model.TryGetValue(smtName, out var val))
            {
                cePairs.Add($"{JsonString(paramName)}:{JsonString(val)}");
            }
        }

        var symbol = $"{method.ContainingType?.Name}.{method.Name}";
        var shortHash = result.QueryHash.Length >= 12 ? result.QueryHash.Substring(0, 12) : result.QueryHash;
        var reasonPart = !string.IsNullOrWhiteSpace(ob.Reason)
            ? $",\"reason\":{JsonString(ob.Reason!.Trim())}"
            : string.Empty;
        var json =
            $"{{\"rule\":\"VER102\",\"symbol\":{JsonString(symbol)},\"kind\":{JsonString(ob.Kind)},\"contract\":{JsonString(ob.ContractText)}{reasonPart},\"counterexample\":{{{string.Join(",", cePairs)}}},\"smtHash\":{JsonString(shortHash)},\"fix\":{JsonString(fixHint)}}}";
        return $"<verifiable-counterexample>{json}</verifiable-counterexample>";
    }

    private static string JsonString(string text)
    {
        var sb = new StringBuilder(text.Length + 4);
        sb.Append('"');
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\r': sb.Append("\\r"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(ch); break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}
