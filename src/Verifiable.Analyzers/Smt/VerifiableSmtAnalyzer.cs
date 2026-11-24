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

                if (!MethodNeedsSmtCheck(method, opCtx.OperationBlocks))
                {
                    return;
                }

                var sourceTree = opCtx.OperationBlocks.FirstOrDefault()?.Syntax.SyntaxTree;
                var lowerer = new CfgSsaLowerer(opCtx.Compilation, method);

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
                    opCtx.ReportDiagnostic(Diagnostic.Create(
                        VerifiableDescriptors.InvalidContractExpression,
                        err.Location,
                        err.Contract,
                        $"{method.ContainingType?.Name}.{method.Name}",
                        err.Error));
                }

                foreach (var ob in allObligations)
                {
                    var script = BuildQueryScript(ob);
                    var result = Z3SolverRunner.Execute(script, msbuildZ3Path, sourceTree?.FilePath);

                    if (result.Status == SmtStatus.SolverUnavailable)
                    {
                        if (Interlocked.Exchange(ref reportedUnavailable, 1) == 0)
                        {
                            opCtx.ReportDiagnostic(Diagnostic.Create(
                                VerifiableDescriptors.Z3SolverUnavailable,
                                method.Locations.FirstOrDefault() ?? ob.Location));
                        }
                        return;
                    }

                    if (result.Status == SmtStatus.Sat)
                    {
                        var ceSummary = FormatCounterexampleSummary(ob, result.Model);
                        var fixHint = GetFixHint(ob.Kind);
                        var fullFix = isAgentMode
                            ? $"{fixHint} {BuildAgentJsonBlock(method, ob, result, fixHint)}"
                            : fixHint;

                        opCtx.ReportDiagnostic(Diagnostic.Create(
                            VerifiableDescriptors.ProofObligationFailed,
                            ob.Location,
                            ob.ContractText,
                            $"{method.ContainingType?.Name}.{method.Name}",
                            ceSummary,
                            fullFix));
                    }
                    else if (result.Status is SmtStatus.Unknown or SmtStatus.Error)
                    {
                        opCtx.ReportDiagnostic(Diagnostic.Create(
                            VerifiableDescriptors.SmtSolverUnknown,
                            ob.Location,
                            ob.ContractText,
                            $"{method.ContainingType?.Name}.{method.Name}",
                            result.ErrorMessage ?? result.Status.ToString()));
                    }
                }
            });
        });
    }

    private static string BuildQueryScript(ProofObligation ob)
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

    private static bool MethodNeedsSmtCheck(IMethodSymbol method, ImmutableArray<IOperation> blocks)
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
                if (op is IInvocationOperation inv)
                {
                    if (inv.TargetMethod.ContainingType?.ToDisplayString() == VerifiableSymbolHelpers.SpecTypeName)
                    {
                        return true;
                    }

                    if (inv.TargetMethod.Name == "From" &&
                        inv.TargetMethod.ContainingType is { } ct &&
                        SmtTypeSystem.TryGetConstrainedUnderlying(ct, out _))
                    {
                        return true;
                    }

                    if (CfgSsaLowerer.MethodHasSpecRequires(inv.TargetMethod))
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
            ? $"counterexample {{ {string.Join(", ", bindings)} }}"
            : "counterexample exists (solver found a violating assignment)";
    }

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
        var json =
            $"{{\"rule\":\"VER102\",\"symbol\":{JsonString(symbol)},\"kind\":{JsonString(ob.Kind)},\"contract\":{JsonString(ob.ContractText)},\"counterexample\":{{{string.Join(",", cePairs)}}},\"smtHash\":{JsonString(shortHash)},\"fix\":{JsonString(fixHint)}}}";
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
