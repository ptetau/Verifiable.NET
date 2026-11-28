using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Verifiable.Core;

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
            VerifiableDescriptors.Z3SolverUnavailable,
            VerifiableDescriptors.ContradictoryPremises,
            VerifiableDescriptors.VacuityCheckUndecided
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
            globalOptions.TryGetValue("build_property.VerifiableClosedEnums", out var msbuildClosedEnums);
            globalOptions.TryGetValue("build_property.VerifiableNonDefaultImmutableArrays", out var msbuildNonDefaultImmArr);
            globalOptions.TryGetValue("build_property.VerifiableAllowMissingZ3", out var msbuildAllowMissingZ3);

            var envAgent = Environment.GetEnvironmentVariable("VERIFIABLE_AGENT");
            var isAgentMode =
                envAgent == "1" || string.Equals(envAgent, "true", StringComparison.OrdinalIgnoreCase) ||
                msbuildAgent == "1" || string.Equals(msbuildAgent, "true", StringComparison.OrdinalIgnoreCase);

            var closedEnums =
                msbuildClosedEnums == "1" || string.Equals(msbuildClosedEnums, "true", StringComparison.OrdinalIgnoreCase);

            var globalNonDefaultImmArr =
                msbuildNonDefaultImmArr == "1" || string.Equals(msbuildNonDefaultImmArr, "true", StringComparison.OrdinalIgnoreCase);

            var allowMissingSolver =
                msbuildAllowMissingZ3 == "1" || string.Equals(msbuildAllowMissingZ3, "true", StringComparison.OrdinalIgnoreCase);

            var reportedUnavailable = 0;

            compilationCtx.RegisterOperationBlockAction(opCtx =>
            {
                if (opCtx.OwningSymbol is not IMethodSymbol method) return;
                if (VerifiableSymbolHelpers.IsSynthesizedRecordMember(method) ||
                    VerifiableSymbolHelpers.IsGeneratedByVerifiableGenerators(method) ||
                    VerifiableSymbolHelpers.HasAttribute(method, VerifiableSymbolHelpers.PureAxiomAttr) ||
                    VerifiableSymbolHelpers.TryGetGeneratedRegexAttribute(method, out _, out _))
                {
                    return;
                }

                if (VerifiableSymbolHelpers.GetScope(method) != VerificationScope.Verifiable)
                {
                    return;
                }

                var sourceTree = opCtx.OperationBlocks.FirstOrDefault()?.Syntax.SyntaxTree;
                var nonDefaultImmArr = globalNonDefaultImmArr;
                if (sourceTree is not null &&
                    opCtx.Options.AnalyzerConfigOptionsProvider.GetOptions(sourceTree)
                        .TryGetValue("verifiable.non_default_immutable_arrays", out var treeNonDefault) &&
                    !string.IsNullOrWhiteSpace(treeNonDefault))
                {
                    nonDefaultImmArr =
                        treeNonDefault == "1" || string.Equals(treeNonDefault, "true", StringComparison.OrdinalIgnoreCase);
                }

                if (!MethodNeedsSmtCheck(opCtx.Compilation, method, opCtx.OperationBlocks, closedEnums, nonDefaultImmArr))
                {
                    return;
                }

                var lowerer = new CfgSsaLowerer(
                    opCtx.Compilation,
                    method,
                    closedEnums: closedEnums,
                    nonDefaultImmutableArrays: nonDefaultImmArr);

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
                        fixHint: "Rewrite the predicate using supported deterministic C# boolean, arithmetic, string, or immutable collection expressions.",
                        summary: $"Unsupported contract in {method.ContainingType?.Name}.{method.Name}: {OneLine(err.Contract)} — {OneLine(err.Error)}");
                    opCtx.ReportDiagnostic(Diagnostic.Create(
                        VerifiableDescriptors.InvalidContractExpression,
                        err.Location,
                        elmErr));
                }

                var reportedFailures = new HashSet<(int Start, int Length, string Kind, string Contract)>();
                var longestRefutedPreamble = -1;

                foreach (var ob in allObligations)
                {
                    var script = BuildQueryScript(ob);
                    var satEvals = SmtCounterexampleAnalyzer.BuildSatEvalExprs(ob.ResultSmtTerm, ob.GoalClauses);
                    var result = Z3SolverRunner.Execute(script, msbuildZ3Path, sourceTree?.FilePath, satEvalExprs: satEvals);
                    var verdict = ProofKernel.Decide(result.Status);

                    if (verdict == ObligationVerdict.Proved)
                    {
                        continue;
                    }

                    if (verdict == ObligationVerdict.SolverMissing)
                    {
                        if (Interlocked.Exchange(ref reportedUnavailable, 1) == 0)
                        {
                            var z3Loc = method.Locations.FirstOrDefault() ?? ob.Location;
                            var blocks = ProofKernel.BlocksBuild(verdict, allowMissingSolver);
                            var elmMissing = ElmDiagnosticFormatter.Format(
                                "Z3 SOLVER NOT FOUND",
                                z3Loc,
                                blocks
                                    ? "The Z3 SMT solver executable could not be found, so the proofs that need it were not checked. The build stops because unchecked code must not pass as verified."
                                    : "The Z3 SMT solver executable could not be found, so compile-time proof checks were skipped (`VerifiableAllowMissingZ3` is set).",
                                explanation: "Checked `VERIFIABLE_Z3_PATH`, `.tools/z3`, `~/.verifiable/z3`, and `PATH`.",
                                fixHint: blocks
                                    ? "Install the `Verifiable.Z3` NuGet package (included with `Verifiable.NET`), pass `-p:VerifiableZ3Path=...`, set `VERIFIABLE_Z3_PATH`, or install `z3` on `PATH`. To build without proofs on purpose, set `<VerifiableAllowMissingZ3>true</VerifiableAllowMissingZ3>`."
                                    : "Install the `Verifiable.Z3` NuGet package (included with `Verifiable.NET`), pass `-p:VerifiableZ3Path=...`, set `VERIFIABLE_Z3_PATH`, or install `z3` on `PATH`.",
                                summary: blocks
                                    ? "Z3 not found, so proofs were not checked. Install Z3 (Verifiable.Z3 package, scripts/install-z3, VERIFIABLE_Z3_PATH or PATH), or set <VerifiableAllowMissingZ3>true</VerifiableAllowMissingZ3> to build without proofs."
                                    : "Z3 not found; proof checks skipped because VerifiableAllowMissingZ3 is set.");
                            // Reported once per compilation and without a source location, so a `#pragma` or per-file
                            // setting in whichever file happened to reach Z3 first cannot switch off the whole check.
                            opCtx.ReportDiagnostic(Diagnostic.Create(
                                VerifiableDescriptors.Z3SolverUnavailable,
                                Location.None,
                                blocks ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
                                additionalLocations: null,
                                properties: null,
                                elmMissing));
                        }
                        return;
                    }

                    if (verdict == ObligationVerdict.Refuted)
                    {
                        longestRefutedPreamble = Math.Max(longestRefutedPreamble, ob.ScriptPreamble.Length);
                        var dedupKey = (ob.Location.SourceSpan.Start, ob.Location.SourceSpan.Length, ob.Kind, ob.ContractText);
                        if (!reportedFailures.Add(dedupKey))
                        {
                            continue;
                        }

                        var insight = SmtCounterexampleAnalyzer.Analyze(result.Model, ob.GoalClauses);
                        var ceSummary = FormatCounterexampleSummary(ob, result.Model, insight.EvaluatedResult);
                        var baseFixHint = GetFixHint(ob.Kind);
                        var combinedFixHint = insight.BoundaryHint is not null
                            ? $"{baseFixHint} {insight.BoundaryHint}"
                            : baseFixHint;
                        var agentPayload = isAgentMode
                            ? BuildAgentJsonBlock(method, ob, result, combinedFixHint)
                            : null;
                        var failSummary =
                            $"{ob.Kind} of {method.ContainingType?.Name}.{method.Name} fails: " +
                            OneLine(ob.Reason ?? ob.ContractText) + "; " + OneLine(ceSummary);
                        var elmFail = ElmDiagnosticFormatter.Format(
                            "PROOF FAILED",
                            ob.Location,
                            $"Z3 found inputs that break a {FormatKindLabel(ob.Kind)} in `{method.ContainingType?.Name}.{method.Name}`.",
                            spirit: ob.Reason,
                            contract: $"{ob.Kind}: {ob.ContractText}",
                            counterexample: ceSummary,
                            explanation: insight.FailedClauseBlock,
                            fixHint: combinedFixHint,
                            agentPayload: agentPayload,
                            summary: failSummary,
                            relatedLocation: ob.ContractLocation,
                            relatedHeading: ob.ContractLocationLabel);

                        var additionalLocs = ob.ContractLocation is { IsInSource: true } cl && cl != Location.None
                            ? new[] { cl }
                            : null;

                        opCtx.ReportDiagnostic(Diagnostic.Create(
                            VerifiableDescriptors.ProofObligationFailed,
                            ob.Location,
                            additionalLocations: additionalLocs,
                            elmFail));
                    }
                    else
                    {
                        // ObligationVerdict.Undecided: never silently treated as proved.
                        var unknownSummary =
                            $"Z3 returned {result.Status} for {method.ContainingType?.Name}.{method.Name}: {OneLine(ob.ContractText)}" +
                            (string.IsNullOrWhiteSpace(result.ErrorMessage) ? string.Empty : $" — {OneLine(result.ErrorMessage!)}");
                        var elmUnknown = ElmDiagnosticFormatter.Format(
                            "SOLVER COULD NOT DECIDE",
                            ob.Location,
                            $"Z3 returned `{result.Status}` while checking `{ob.ContractText}` in `{method.ContainingType?.Name}.{method.Name}`.",
                            spirit: ob.Reason,
                            explanation: string.IsNullOrWhiteSpace(result.ErrorMessage) ? null : result.ErrorMessage,
                            fixHint: "Simplify the contract, add `Spec.LoopInvariant` or `Spec.Decreases` annotations, or factor complex steps into helper lemmas.",
                            summary: unknownSummary,
                            relatedLocation: ob.ContractLocation,
                            relatedHeading: ob.ContractLocationLabel);
                        opCtx.ReportDiagnostic(Diagnostic.Create(
                            VerifiableDescriptors.SmtSolverUnknown,
                            ob.Location,
                            elmUnknown));
                    }
                }

                // Vacuity: if the premises themselves contradict, every goal above was "proved" without meaning
                // anything. Assertions only accumulate, so the longest preamble contains every premise of the method.
                // A refuted goal shows its own premises are satisfiable, which settles it only if it saw all of them.
                if (allObligations.Count > 0)
                {
                    var vacuity = new VacuityCheck(opCtx, method, closedEnums, nonDefaultImmArr, msbuildZ3Path, sourceTree?.FilePath);
                    var allPremises = allObligations.OrderByDescending(o => o.ScriptPreamble.Length).First().ScriptPreamble;
                    var premises = longestRefutedPreamble < allPremises.Length
                        ? vacuity.Run(allPremises)
                        : SmtStatus.Sat;

                    if (ProofKernel.PremisesContradict(premises))
                    {
                        vacuity.ReportContradictoryPremises();
                    }
                    else if (VacuityKernel.CheckUndecided(premises))
                    {
                        vacuity.Undecided.Add("whether the method's assumptions can all hold at once");
                    }
                    else
                    {
                        // The premises can hold as a whole, but a callee guarantee may still be impossible on one path.
                        vacuity.CheckCalleeGuarantees(lowerer);
                    }

                    vacuity.ReportUndecided();
                }
            });
        });
    }

    /// <summary>Premise checks Z3 could not decide, remembered for the life of the process so they are not retried on every build.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> UndecidedPremiseChecks = new(StringComparer.Ordinal);

    private const int PremiseCheckTimeoutMs = 1000;

    /// <summary>
    /// Checks that the assumptions behind a method's proofs can hold: all of them at once (<c>VER105</c>), and each
    /// callee guarantee at the call that assumes it (<c>VER105</c> at the call). Checks Z3 cannot decide are
    /// collected and reported once per method (<c>VER106</c>).
    /// </summary>
    private sealed class VacuityCheck(
        OperationBlockAnalysisContext opCtx,
        IMethodSymbol method,
        bool closedEnums,
        bool nonDefaultImmArr,
        string? msbuildZ3Path,
        string? sourceFilePath)
    {
        private CfgSsaLowerer? _tracking;
        private List<ProofObligation>? _trackedObligations;

        private string Display => $"{method.ContainingType?.Name}.{method.Name}";

        /// <summary>Descriptions of the checks Z3 could not decide.</summary>
        public List<string> Undecided { get; } = new();

        /// <summary>Runs one premise check with the short timeout. Undecided scripts are remembered and not retried.</summary>
        public SmtStatus Run(string script)
        {
            var key = Z3SolverRunner.ComputeSha256(script);
            if (UndecidedPremiseChecks.ContainsKey(key)) return SmtStatus.Unknown;

            var status = Z3SolverRunner.Execute(script, msbuildZ3Path, sourceFilePath, timeoutMs: PremiseCheckTimeoutMs).Status;
            if (VacuityKernel.CheckUndecided(status))
            {
                UndecidedPremiseChecks.TryAdd(key, 0);
            }

            return status;
        }

        /// <summary>
        /// Checks each callee guarantee against the assumptions made before it. Called only when the method's
        /// premises as a whole can hold.
        /// </summary>
        public void CheckCalleeGuarantees(CfgSsaLowerer lowerer)
        {
            var guarantees = lowerer.CalleeGuarantees;
            for (var i = 0; i < guarantees.Count; i++)
            {
                var g = guarantees[i];

                // Only goals emitted after the guarantee can rely on it.
                if (g.ObligationsBefore >= lowerer.ObligationCount) continue;

                // An unconditional guarantee is part of the premise set that was just shown to hold.
                if (g.Context == "true") continue;

                var prefix = lowerer.PremisePrefix(g.AssertionsBefore);
                var withGuarantee = Run(VacuityKernel.GuaranteeQuery(prefix, g.Context, g.Guarantee));
                var withoutGuarantee = VacuityKernel.NeedsReachabilityCheck(withGuarantee)
                    ? Run(VacuityKernel.ReachabilityQuery(prefix, g.Context))
                    : withGuarantee;

                var verdict = VacuityKernel.ClassifyGuarantee(withGuarantee, withoutGuarantee);
                if (verdict == GuaranteeVerdict.Impossible)
                {
                    ReportImpossibleGuarantee(i, g);
                }
                else if (verdict == GuaranteeVerdict.Undecided)
                {
                    Undecided.Add($"whether `{g.Label}` can hold at the call on line {g.CallLocation.GetLineSpan().StartLinePosition.Line + 1}");
                }
            }
        }

        public void ReportContradictoryPremises()
        {
            var lines = new List<string>();
            var obligations = TrackedObligations();
            if (obligations.Count > 0)
            {
                var namedPremises = obligations.OrderByDescending(o => o.ScriptPreamble.Length).First().ScriptPreamble;
                lines.AddRange(ExplainConflict(namedPremises));
            }

            var loc = method.Locations.FirstOrDefault() ?? Location.None;
            var conflict = lines.Count > 0
                ? "These assumptions cannot all hold at once:\n    - " + string.Join("\n    - ", lines)
                : "Z3 could not narrow the conflict down to named assumptions.";
            var elm = ElmDiagnosticFormatter.Format(
                "CONTRADICTORY PREMISES",
                loc,
                $"No input satisfies the assumptions of `{Display}`, so Z3 would \"prove\" any contract here, true or false. Its proofs have been rejected.",
                explanation: conflict,
                fixHint: "Fix: Correct the conflicting rules (`Spec.Requires`, `[Invariant]`, `[TypeAlias<T>]`, or a callee's `Spec.Ensures` / `[PureAxiom]`) so that at least one input satisfies them all. Note that a `Spec.Requires` anywhere in a method counts as an entry precondition.",
                summary: $"Premises of {Display} contradict each other, so its proofs are vacuous" +
                         (lines.Count > 0 ? ": " + OneLine(string.Join("; ", lines)) : "."));
            opCtx.ReportDiagnostic(Diagnostic.Create(VerifiableDescriptors.ContradictoryPremises, loc, elm));
        }

        private void ReportImpossibleGuarantee(int index, CalleeGuarantee g)
        {
            // The lowering is deterministic, so the tracked run records the same guarantee at the same index.
            TrackedObligations();
            var lines = new List<string>();
            if (_tracking!.CalleeGuarantees.Count > index)
            {
                var tracked = _tracking.CalleeGuarantees[index];
                var named = _tracking.PremisePrefix(tracked.AssertionsBefore) +
                            $"\n(assert (! {tracked.Context} :named vacuity_call_context))" +
                            $"\n(assert (! {tracked.Guarantee} :named vacuity_guarantee))\n";
                lines.AddRange(ExplainConflict(named));
            }

            var conflict = lines.Count > 0
                ? "Given the path to this call and the callee's preconditions, it clashes with:\n    - " + string.Join("\n    - ", lines)
                : "It contradicts itself, the path to this call, or the callee's preconditions.";
            var elm = ElmDiagnosticFormatter.Format(
                "CONTRADICTORY PREMISES",
                g.CallLocation,
                $"The guarantee `{g.Label}` cannot hold where `{Display}` calls it, although the call is reachable. Every goal after the call on this path would be \"proved\" without meaning anything, so they have been rejected.",
                spirit: g.Reason,
                explanation: conflict,
                fixHint: "Fix: Correct the callee's `Spec.Ensures` / `[PureAxiom]` so that it can hold for every input its preconditions allow, or correct the assumptions it clashes with.",
                summary: $"Guarantee {OneLine(g.Label)} cannot hold where {Display} calls it, so proofs after the call are vacuous: fix the guarantee" +
                         (lines.Count > 0 ? " or what it clashes with: " + OneLine(string.Join("; ", lines)) : "."),
                relatedLocation: g.GuaranteeLocation,
                relatedHeading: "The guarantee is declared here:");
            var additional = g.GuaranteeLocation is { IsInSource: true } gl && gl != g.CallLocation ? new[] { gl } : null;
            opCtx.ReportDiagnostic(Diagnostic.Create(VerifiableDescriptors.ContradictoryPremises, g.CallLocation, additionalLocations: additional, elm));
        }

        public void ReportUndecided()
        {
            if (Undecided.Count == 0) return;

            var loc = method.Locations.FirstOrDefault() ?? Location.None;
            var elm = ElmDiagnosticFormatter.Format(
                "VACUITY CHECK UNDECIDED",
                loc,
                $"Z3 could not decide, within {PremiseCheckTimeoutMs / 1000} s, whether the assumptions behind the proofs in `{Display}` can hold. If they cannot, those proofs are vacuous: they would pass even for false contracts.",
                explanation: "Undecided:\n    - " + string.Join("\n    - ", Undecided),
                fixHint: "Fix: Simplify the contracts this method relies on (its `Spec.Requires`, the types it uses, and the `Spec.Ensures` of the methods it calls), especially non-linear arithmetic and quantifiers, or split the method into smaller helpers with their own contracts.",
                summary: $"Could not check that the assumptions of {Display} can hold, so its proofs may be vacuous: simplify the contracts it relies on or split it into helpers.");
            opCtx.ReportDiagnostic(Diagnostic.Create(VerifiableDescriptors.VacuityCheckUndecided, loc, elm));
        }

        /// <summary>Lowers the method again with named premises, so an unsat core can say which assumptions clash.</summary>
        private List<ProofObligation> TrackedObligations()
        {
            if (_trackedObligations is not null) return _trackedObligations;

            _tracking = new CfgSsaLowerer(
                opCtx.Compilation,
                method,
                trackProofPremises: true,
                closedEnums: closedEnums,
                nonDefaultImmutableArrays: nonDefaultImmArr);
            _trackedObligations = new List<ProofObligation>();
            foreach (var block in opCtx.OperationBlocks)
            {
                if (block is IBlockOperation or IMethodBodyOperation or IConstructorBodyOperation)
                {
                    _trackedObligations.AddRange(_tracking.Lower(opCtx.GetControlFlowGraph(block), opCtx.OperationBlocks).Obligations);
                }
            }

            return _trackedObligations;
        }

        /// <summary>Names the assumptions in the unsat core of a script of named premises.</summary>
        private List<string> ExplainConflict(string namedScript)
        {
            var lines = new List<string>();
            var core = Z3SolverRunner.ExecuteWithUnsatCore(namedScript, msbuildZ3Path, sourceFilePath);
            if (core.Status != SmtStatus.Unsat) return lines;

            var byId = _tracking!.Premises.ToDictionary(p => p.Id, StringComparer.Ordinal);
            foreach (var id in core.UnsatCore)
            {
                if (!byId.TryGetValue(id, out var premise) || premise.Kind == "SsaBinding") continue;
                var where = premise.FilePath is null
                    ? string.Empty
                    : $" ({System.IO.Path.GetFileName(premise.FilePath)}:{premise.Line})";
                var why = string.IsNullOrWhiteSpace(premise.Reason) || premise.Label.Contains(premise.Reason!.Trim())
                    ? string.Empty
                    : $" — {premise.Reason!.Trim()}";
                lines.Add($"{premise.Kind}: {premise.Label}{where}{why}");
            }

            return lines;
        }
    }

    internal static string BuildQueryScript(ProofObligation ob) =>
        ProofKernel.BuildRefutationQuery(ob.ScriptPreamble, ob.PathCondition ?? string.Empty, ob.GoalTerm);

    private static bool MethodNeedsSmtCheck(
        Compilation compilation,
        IMethodSymbol method,
        ImmutableArray<IOperation> blocks,
        bool closedEnums,
        bool nonDefaultImmutableArrays)
    {
        if (VerifiableSymbolHelpers.HasAttribute(method, VerifiableSymbolHelpers.CheckedArithmeticAttr) ||
            (method.ContainingType is not null && VerifiableSymbolHelpers.HasAttribute(method.ContainingType, VerifiableSymbolHelpers.CheckedArithmeticAttr)) ||
            (method.ContainingAssembly is not null && VerifiableSymbolHelpers.HasAttribute(method.ContainingAssembly, VerifiableSymbolHelpers.CheckedArithmeticAttr)) ||
            SmtTypeSystem.HasAnyInvariants(method.ReturnType) ||
            IsResultWithInvariantPayload(method.ReturnType) ||
            IsClosedEnumType(method.ReturnType, closedEnums) ||
            VerifiableSymbolHelpers.IsNonDefaultReturn(method, nonDefaultImmutableArrays) ||
            SmtTypeSystem.HasAnyNonDefaultImmutableArray(method.ReturnType, nonDefaultImmutableArrays))
        {
            return true;
        }

        foreach (var b in blocks)
        {
            foreach (var op in b.DescendantsAndSelf())
            {
                if (op is IConversionOperation convOp && CfgSsaLowerer.IsConversionChecked(convOp, compilation))
                {
                    return true;
                }

                if (op is IObjectCreationOperation objOp && objOp.Type is not null &&
                    SmtTypeSystem.HasAnyNonDefaultImmutableArray(objOp.Type, nonDefaultImmutableArrays))
                {
                    return true;
                }

                if (op is IWithOperation withOp && withOp.Type is not null &&
                    SmtTypeSystem.HasAnyNonDefaultImmutableArray(withOp.Type, nonDefaultImmutableArrays))
                {
                    return true;
                }

                if (op is IInvocationOperation inv)
                {
                    var ct = inv.TargetMethod.ContainingType;
                    var ctOrig = ct?.OriginalDefinition.ToDisplayString();

                    if (ct?.ToDisplayString() == VerifiableSymbolHelpers.SpecTypeName)
                    {
                        return true;
                    }

                    if (inv.TargetMethod.Name == "From" && ct is not null &&
                        SmtTypeSystem.TryGetConstrainedUnderlying(ct, out _))
                    {
                        return true;
                    }

                    if (CfgSsaLowerer.MethodHasSpecRequires(compilation, inv.TargetMethod))
                    {
                        return true;
                    }

                    if (inv.TargetMethod.Parameters.Any(p =>
                        IsClosedEnumType(p.Type, closedEnums) ||
                        VerifiableSymbolHelpers.IsNonDefaultParameter(p, nonDefaultImmutableArrays)))
                    {
                        return true;
                    }

                    if (inv.TargetMethod.Name == "Add" &&
                        ctOrig == "System.Collections.Immutable.ImmutableDictionary<TKey, TValue>")
                    {
                        return true;
                    }

                    if (ctOrig == "System.Collections.Immutable.ImmutableArray<T>" &&
                        inv.TargetMethod.Name is "Add" or "SetItem" or "RemoveAt" or "Insert")
                    {
                        return true;
                    }

                    if (ctOrig == "System.Collections.Immutable.ImmutableList<T>" &&
                        inv.TargetMethod.Name is "SetItem" or "RemoveAt" or "Insert")
                    {
                        return true;
                    }

                    if (ctOrig is "System.Linq.Enumerable" or "System.Linq.ImmutableArrayExtensions")
                    {
                        return true;
                    }

                    if (ctOrig == "System.Text.RegularExpressions.Regex" && inv.TargetMethod.Name == "IsMatch")
                    {
                        return true;
                    }

                    if (ct?.SpecialType == SpecialType.System_String &&
                        !inv.TargetMethod.IsStatic &&
                        inv.TargetMethod.Name is "Trim" or "Contains" or "StartsWith" or "EndsWith")
                    {
                        return true;
                    }

                    if (CalendarArithmetic.IsTrustedCalendarMethod(inv.TargetMethod))
                    {
                        return true;
                    }
                }

                if (op is IBinaryOperation bin &&
                    (CalendarArithmetic.Classify(bin.LeftOperand.Type) != CalendarTypeKind.None ||
                     CalendarArithmetic.Classify(bin.RightOperand.Type) != CalendarTypeKind.None) &&
                    bin.OperatorKind is BinaryOperatorKind.Add or BinaryOperatorKind.Subtract)
                {
                    return true;
                }

                if (op is IUnaryOperation { OperatorKind: UnaryOperatorKind.Minus } un &&
                    CalendarArithmetic.Classify(un.Operand.Type) == CalendarTypeKind.TimeSpan)
                {
                    return true;
                }

                if (op is IPropertyReferenceOperation pr)
                {
                    if (pr.Property.IsIndexer)
                    {
                        return true;
                    }
                    if (pr.Instance?.Type?.OriginalDefinition.ToDisplayString() == "System.Collections.Immutable.ImmutableArray<T>" &&
                        pr.Property.Name is "Length" or "Count" or "IsEmpty")
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool IsClosedEnumType(ITypeSymbol type, bool closedEnums)
    {
        if (type is INamedTypeSymbol nullable &&
            nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
            nullable.TypeArguments.Length == 1)
        {
            type = nullable.TypeArguments[0];
        }

        if (type is not INamedTypeSymbol nt || nt.TypeKind != TypeKind.Enum)
        {
            return false;
        }

        if (nt.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "System.FlagsAttribute"))
        {
            return false;
        }

        return closedEnums || VerifiableSymbolHelpers.HasAttribute(nt, VerifiableSymbolHelpers.VerifiableAttr);
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

    private static string FormatCounterexampleSummary(
        ProofObligation ob,
        IReadOnlyDictionary<string, string> model,
        string? evaluatedResult = null)
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
            foreach (var kv in model.Where(k =>
                !k.Key.StartsWith("ssa_", StringComparison.Ordinal) &&
                !k.Key.StartsWith(SmtCounterexampleAnalyzer.EvalKeyPrefix, StringComparison.Ordinal)).Take(4))
            {
                bindings.Add($"{kv.Key} = {kv.Value}");
            }
        }

        if (!string.IsNullOrWhiteSpace(evaluatedResult) &&
            !bindings.Any(b => b.StartsWith("result =", StringComparison.Ordinal)))
        {
            bindings.Add($"result = {evaluatedResult}");
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
