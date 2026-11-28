using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Verifiable.Analyzers;
using Verifiable.Analyzers.Smt;
using Verifiable.Core;

namespace Verifiable.Query;

internal static class QueryEngine
{
    public static SymbolInventory ListSymbols(CSharpCompilation compilation)
    {
        var constrained = new List<ListedConstrainedType>();
        var invariants = new List<ListedInvariant>();
        var methods = new List<ListedMethod>();

        foreach (var tree in compilation.SyntaxTrees)
        {
            if (IsGeneratedTree(tree.FilePath)) continue;

            var model = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();

            foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(typeDecl) is not INamedTypeSymbol typeSym) continue;
                var (file, line) = GetLocationInfo(typeDecl.GetLocation());

                if (SmtTypeSystem.TryGetConstrainedUnderlying(typeSym, out var underlying, out var desc) && underlying is not null)
                {
                    var pred = "";
                    if (SmtTypeSystem.TryGetIsValidMethod(typeSym, out var isValidSym) && isValidSym is not null)
                    {
                        var md = isValidSym.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() as MethodDeclarationSyntax;
                        pred = md?.ExpressionBody?.Expression.ToString() ?? "IsValid(...)";
                    }
                    constrained.Add(new ListedConstrainedType(
                        typeSym.Name,
                        typeSym.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        underlying.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        pred,
                        desc,
                        file,
                        line));
                }

                foreach (var invSym in SmtTypeSystem.GetInvariantMembers(typeSym))
                {
                    if (!SymbolEqualityComparer.Default.Equals(invSym.ContainingType, typeSym)) continue;
                    var (invFile, invLine) = GetLocationInfo(invSym.Locations.FirstOrDefault() ?? typeDecl.GetLocation());
                    invariants.Add(new ListedInvariant(
                        typeSym.Name,
                        invSym.Name,
                        SmtTypeSystem.GetInvariantDescription(invSym),
                        invFile,
                        invLine));
                }
            }

            foreach (var methodDecl in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(methodDecl) is not IMethodSymbol m) continue;
                if (!IsQueryableUserMethod(m)) continue;

                var (mFile, mLine) = GetLocationInfo(methodDecl.GetLocation());
                var (reqCount, ensCount) = CountMethodSpecs(methodDecl);
                var paramSig = string.Join(", ", m.Parameters.Select(p =>
                    $"{p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {p.Name}"));
                methods.Add(new ListedMethod(
                    $"{m.ContainingType?.Name}.{m.Name}",
                    $"{m.ContainingType?.Name}.{m.Name}({paramSig})",
                    m.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    VerifiableSymbolHelpers.HasAttribute(m, VerifiableSymbolHelpers.InboundPortAttr),
                    reqCount,
                    ensCount,
                    mFile,
                    mLine));
            }
        }

        return new SymbolInventory(constrained, invariants, methods);
    }

    public static QueryReport Ask(
        CSharpCompilation compilation,
        string? targetName,
        bool allVerifiable,
        string assertText,
        string? assumeText,
        string? because,
        string? z3Path,
        int timeoutMs)
    {
        var allMethods = CollectQueryableMethods(compilation);
        if (allMethods.Count == 0)
        {
            return ErrorReport("ask", assertText, assumeText, because, null,
                "NO QUERYABLE METHODS",
                "No `[Verifiable]` value-returning methods were found in the supplied sources.",
                Location.None,
                "Ensure your project or file marks the target class/assembly `[Verifiable]` (or use `verifiable-query list` to inspect symbols).");
        }

        List<IMethodSymbol> matched;
        if (allVerifiable)
        {
            matched = allMethods;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(targetName))
            {
                return ErrorReport("ask", assertText, assumeText, because, null,
                    "MISSING TARGET",
                    "No `--target` method/class or `--all` flag was specified.",
                    Location.None,
                    "Pass `--target TypeName.MethodName` (e.g. `LedgerEngine.PostTransfer`), `--target TypeName`, or `--all`.");
            }

            matched = ResolveTargets(allMethods, targetName!, out var resolveError, out var resolveFix);
            if (resolveError is not null)
            {
                return ErrorReport("ask", assertText, assumeText, because, targetName,
                    "TARGET NOT FOUND",
                    resolveError,
                    Location.None,
                    resolveFix);
            }
        }

        // Single target: fail immediately with helpful compilation error if expression doesn't bind.
        if (matched.Count == 1)
        {
            var singleRes = EvaluateMethod(compilation, matched[0], assertText, assumeText, because, z3Path, timeoutMs);
            if (singleRes.Status == QueryOutcomeStatus.Error)
            {
                return ErrorReport("ask", assertText, assumeText, because, $"{matched[0].ContainingType?.Name}.{matched[0].Name}",
                    "QUERY EXPRESSION ERROR",
                    singleRes.ErrorMessage ?? "Query could not be bound.",
                    singleRes.MethodLocation,
                    singleRes.Hint);
            }
            return BuildReport("ask", assertText, assumeText, because, null, [singleRes]);
        }

        // Multi-method sweep: skip methods where the claim doesn't type-check ONLY if some bind cleanly; if none bind, report the first bind error.
        var results = new List<MethodQueryResult>();
        var skippedErrors = new List<MethodQueryResult>();
        foreach (var m in matched)
        {
            var r = EvaluateMethod(compilation, m, assertText, assumeText, because, z3Path, timeoutMs);
            if (r.Status == QueryOutcomeStatus.Error &&
                r.ErrorMessage is not null &&
                r.ErrorMessage.StartsWith("Could not compile query expression", StringComparison.Ordinal))
            {
                skippedErrors.Add(r);
                continue;
            }
            results.Add(r);
        }

        if (results.Count == 0 && skippedErrors.Count > 0)
        {
            var first = skippedErrors[0];
            return ErrorReport("ask", assertText, assumeText, because, targetName,
                "QUERY EXPRESSION ERROR",
                $"Expression `{assertText}` did not bind to any method in `{targetName}`: {first.ErrorMessage}",
                first.MethodLocation,
                first.Hint);
        }

        return BuildReport("ask", assertText, assumeText, because, targetName, results);
    }

    public static QueryReport CheckInvariant(
        CSharpCompilation compilation,
        string typeName,
        string exprText,
        string? assumeText,
        string? because,
        string? z3Path,
        int timeoutMs)
    {
        if (!QueryExpressionBinder.TryParseLambdaClaim(exprText, out var paramName, out var bodySyntax, out var parseErr))
        {
            return ErrorReport("check-invariant", exprText, assumeText, because, typeName,
                "INVALID INVARIANT EXPRESSION",
                parseErr!,
                Location.None,
                "Pass a boolean lambda predicate such as `s => s.TotalDebits == s.TotalCredits`.");
        }

        var targetType = ResolveTypeSymbol(compilation, typeName, out var typeErr, out var typeFix);
        if (targetType is null)
        {
            return ErrorReport("check-invariant", exprText, assumeText, because, typeName,
                "TYPE NOT FOUND",
                typeErr!,
                Location.None,
                typeFix ?? "Check the type name with `verifiable-query list`.");
        }

        var candidates = CollectQueryableMethods(compilation);
        var adapters = new List<(IMethodSymbol Method, string AdaptedExpr)>();

        foreach (var m in candidates)
        {
            // Skip [Invariant] members or IsValid methods themselves
            if (VerifiableSymbolHelpers.HasAttribute(m, VerifiableSymbolHelpers.InvariantAttr) || m.Name == "IsValid")
            {
                continue;
            }

            if (TryBuildTypeInvariantProbe(m, targetType, paramName, bodySyntax!, out var adaptedExpr))
            {
                adapters.Add((m, adaptedExpr!));
            }
        }

        if (adapters.Count == 0)
        {
            return ErrorReport("check-invariant", exprText, assumeText, because, targetType.Name,
                "NO PRODUCER METHODS FOUND",
                $"No `[Verifiable]` methods producing `{targetType.Name}` were found in the supplied sources.",
                targetType.Locations.FirstOrDefault() ?? Location.None,
                $"Mark functions that return `{targetType.Name}` (or a wrapper holding `{targetType.Name}`) with `[Verifiable]`.");
        }

        var results = new List<MethodQueryResult>(adapters.Count);
        foreach (var (m, adaptedExpr) in adapters)
        {
            var res = EvaluateMethod(compilation, m, adaptedExpr, assumeText, because, z3Path, timeoutMs);
            if (res.Status == QueryOutcomeStatus.Error && results.Count == 0 && adapters.Count == 1)
            {
                return ErrorReport("check-invariant", exprText, assumeText, because, targetType.Name,
                    "QUERY EXPRESSION ERROR",
                    res.ErrorMessage ?? "Query could not be bound.",
                    res.MethodLocation,
                    res.Hint);
            }
            results.Add(res);
        }

        if (results.All(r => r.Status == QueryOutcomeStatus.Error))
        {
            var first = results[0];
            return ErrorReport("check-invariant", exprText, assumeText, because, targetType.Name,
                "QUERY EXPRESSION ERROR",
                first.ErrorMessage ?? "Failed to bind invariant expression.",
                first.MethodLocation,
                first.Hint);
        }

        return BuildReport("check-invariant", exprText, assumeText, because, targetType.Name, results);
    }

    public static MethodQueryResult EvaluateMethod(
        CSharpCompilation compilation,
        IMethodSymbol targetMethod,
        string assertText,
        string? assumeText,
        string? because,
        string? z3Path,
        int timeoutMs)
    {
        var methodDisplay = $"{targetMethod.ContainingType?.Name}.{targetMethod.Name}";
        var returnTypeDisplay = targetMethod.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        var methodLoc = targetMethod.Locations.FirstOrDefault() ?? Location.None;

        // R4: a method with an unwritten body (Spec.Hole) is never proved, whatever is asked of it.
        if (targetMethod.DeclaringSyntaxReferences.Any(r => Verifiable.Analyzers.Holes.ContainsHole(r.GetSyntax())))
        {
            return new MethodQueryResult(
                methodDisplay,
                returnTypeDisplay,
                assertText,
                assumeText,
                because,
                QueryOutcomeStatus.Inconclusive,
                false,
                0,
                null,
                methodLoc,
                Array.Empty<QueryReason>(),
                Array.Empty<QueryReason>(),
                Array.Empty<PathCounterexample>(),
                $"`{methodDisplay}` has an unwritten body (Spec.Hole), so nothing about its result can be proved yet.",
                "Write the body, then ask again. `verifiable-query holes` lists every unwritten body.");
        }

        var bindOutcome = QueryExpressionBinder.Bind(compilation, targetMethod, assertText, assumeText);
        if (bindOutcome.Bound is null)
        {
            return new MethodQueryResult(
                methodDisplay,
                returnTypeDisplay,
                assertText,
                assumeText,
                because,
                QueryOutcomeStatus.Error,
                false,
                0,
                null,
                methodLoc,
                Array.Empty<QueryReason>(),
                Array.Empty<QueryReason>(),
                Array.Empty<PathCounterexample>(),
                bindOutcome.ErrorMessage,
                bindOutcome.FixHint);
        }

        var bound = bindOutcome.Bound;
        if (!TryBuildCfg(bound.CompilationWithProbe, bound.TargetMethodInProbe, out var cfg, out var rawBlocks, out var cfgError))
        {
            return new MethodQueryResult(
                methodDisplay,
                returnTypeDisplay,
                assertText,
                assumeText,
                because,
                QueryOutcomeStatus.Error,
                false,
                0,
                null,
                methodLoc,
                Array.Empty<QueryReason>(),
                Array.Empty<QueryReason>(),
                Array.Empty<PathCounterexample>(),
                cfgError,
                null);
        }

        var lowerer = new CfgSsaLowerer(bound.CompilationWithProbe, bound.TargetMethodInProbe, trackProofPremises: true);
        var outcome = lowerer.LowerWithQuery(
            cfg!,
            rawBlocks,
            bound.AssertBodyOp,
            bound.ResultParam,
            bound.ThisParam,
            bound.ForwardedMethodParams,
            assertText,
            bound.AssumeBodyOp,
            bound.AssumeUsesResult,
            assumeText);

        if (outcome.Errors.Count > 0)
        {
            var errMsg = string.Join("; ", outcome.Errors.Select(e => $"{e.Contract}: {e.Error}"));
            return new MethodQueryResult(
                methodDisplay,
                returnTypeDisplay,
                assertText,
                assumeText,
                because,
                QueryOutcomeStatus.Error,
                false,
                0,
                null,
                outcome.Errors[0].Location != Location.None ? outcome.Errors[0].Location : methodLoc,
                Array.Empty<QueryReason>(),
                Array.Empty<QueryReason>(),
                Array.Empty<PathCounterexample>(),
                $"Could not lower expression or contract into SMT: {errMsg}",
                "Rewrite the query using supported pure arithmetic, boolean, record property, or ImmutableList/ImmutableDictionary operations.");
        }

        if (outcome.Obligations.Count == 0)
        {
            return new MethodQueryResult(
                methodDisplay,
                returnTypeDisplay,
                assertText,
                assumeText,
                because,
                QueryOutcomeStatus.Inconclusive,
                false,
                0,
                null,
                methodLoc,
                Array.Empty<QueryReason>(),
                Array.Empty<QueryReason>(),
                Array.Empty<PathCounterexample>(),
                $"No value-returning exit path was found in `{methodDisplay}`.",
                "Ensure the target method has at least one return path.");
        }

        // Check against the premises available at the call, not the exit premises:
        // those can include the callee postcondition or even the assumption being evaluated.
        foreach (var req in outcome.Preconditions)
        {
            var check = Z3SolverRunner.Execute(VerifiableSmtAnalyzer.BuildQueryScript(req), z3Path,
                req.Location.SourceTree?.FilePath, timeoutMs);
            var preVerdict = ProofKernel.Decide(check.Status);
            if (preVerdict == ObligationVerdict.Proved) continue;
            return new MethodQueryResult(methodDisplay, returnTypeDisplay, assertText, assumeText, because,
                preVerdict == ObligationVerdict.SolverMissing ? QueryOutcomeStatus.Z3NotFound : QueryOutcomeStatus.Inconclusive,
                false, outcome.Obligations.Count, check.QueryHash, req.Location,
                Array.Empty<QueryReason>(), Array.Empty<QueryReason>(), Array.Empty<PathCounterexample>(),
                $"Cannot prove call precondition: {req.ContractText} (Z3: {check.Status}). {check.ErrorMessage}".TrimEnd(),
                "Guard the call or strengthen the input precondition before relying on the helper.");
        }

        var coreReasons = new List<QueryReason>();
        var coreTraceSteps = new List<QueryReason>();
        var seenReasons = new HashSet<string>(StringComparer.Ordinal);
        var counterexamples = new List<PathCounterexample>();
        var undecidedCount = 0;
        var reachableProvedCount = 0;
        var unreachableCount = 0;
        string? lastHash = null;
        string? unknownMsg = null;

        foreach (var ob in outcome.Obligations)
        {
            var allPremises = ob.Premises.ToDictionary(p => p.Id, StringComparer.Ordinal);
            var script = BuildNamedQueryScript(ob, allPremises);
            var sourceFile = ob.ReturnLocation.SourceTree?.FilePath ?? methodLoc.SourceTree?.FilePath;

            var satEvals = SmtCounterexampleAnalyzer.BuildSatEvalExprs(ob.ResultSmtName, ob.GoalClauses);
            var z3Res = Z3SolverRunner.ExecuteWithUnsatCore(script, z3Path, sourceFile, timeoutMs, satEvalExprs: satEvals);
            lastHash = z3Res.QueryHash;
            var verdict = ProofKernel.Decide(z3Res.Status);

            if (verdict == ObligationVerdict.SolverMissing)
            {
                return new MethodQueryResult(
                    methodDisplay,
                    returnTypeDisplay,
                    assertText,
                    assumeText,
                    because,
                    QueryOutcomeStatus.Z3NotFound,
                    false,
                    outcome.Obligations.Count,
                    null,
                    methodLoc,
                    Array.Empty<QueryReason>(),
                    Array.Empty<QueryReason>(),
                    Array.Empty<PathCounterexample>(),
                    "The Z3 SMT solver executable could not be found.",
                    "Run `verifiable-query tools install z3`, or pass `--z3 <path>`, or set `VERIFIABLE_Z3_PATH`.");
            }

            if (verdict == ObligationVerdict.Refuted)
            {
                var insight = SmtCounterexampleAnalyzer.Analyze(z3Res.Model, ob.GoalClauses);
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (pName, smtName) in ob.TrackedParameters)
                {
                    if (z3Res.Model.TryGetValue(smtName, out var val))
                    {
                        values[pName] = val;
                    }
                }
                if (values.Count == 0)
                {
                    foreach (var kv in z3Res.Model.Where(k =>
                        !k.Key.StartsWith("ssa_", StringComparison.Ordinal) &&
                        !k.Key.StartsWith(SmtCounterexampleAnalyzer.EvalKeyPrefix, StringComparison.Ordinal)).Take(6))
                    {
                        values[kv.Key] = kv.Value;
                    }
                }
                if (!string.IsNullOrWhiteSpace(insight.EvaluatedResult) && !values.ContainsKey("result"))
                {
                    values["result"] = insight.EvaluatedResult!;
                }

                var (retFile, retLine) = GetLocationInfo(ob.ReturnLocation);
                counterexamples.Add(new PathCounterexample(
                    retFile,
                    retLine,
                    ob.ReturnLocation,
                    values,
                    insight.FailedClauseBlock,
                    insight.BoundaryHint));
                continue;
            }

            if (verdict == ObligationVerdict.Undecided)
            {
                undecidedCount++;
                unknownMsg = z3Res.ErrorMessage ?? $"Z3 returned `{z3Res.Status}`.";
                continue;
            }

            // verdict == ObligationVerdict.Proved
            var usedGoal = z3Res.UnsatCore.Contains(ProofKernel.NegatedGoalName, StringComparer.Ordinal);
            if (!usedGoal)
            {
                unreachableCount++;
            }
            else
            {
                reachableProvedCount++;
            }

            foreach (var coreId in z3Res.UnsatCore)
            {
                if (coreId == ProofKernel.NegatedGoalName) continue;
                if (!allPremises.TryGetValue(coreId, out var prem)) continue;

                var key = $"{prem.Kind}|{prem.Label}|{prem.FilePath}|{prem.Line}";
                if (!seenReasons.Add(key)) continue;

                var qr = new QueryReason(
                    prem.Kind,
                    prem.Label,
                    prem.Reason,
                    string.IsNullOrWhiteSpace(prem.FilePath) ? null : Path.GetFileName(prem.FilePath),
                    prem.Line);

                if (prem.Kind == "SsaBinding")
                {
                    coreTraceSteps.Add(qr);
                }
                else
                {
                    coreReasons.Add(qr);
                }
            }
        }

        var overall = QueryKernel.Aggregate(counterexamples.Count, undecidedCount, reachableProvedCount, unreachableCount);

        if (overall == QueryVerdict.Refuted)
        {
            var boundaryHint = counterexamples.Select(c => c.BoundaryHint).FirstOrDefault(h => !string.IsNullOrWhiteSpace(h));
            var baseHint = "Strengthen `Spec.Requires(...)` / `--assume`, enforce a `[TypeAlias<T>]` / `[Invariant]`, or fix the implementation.";
            var fullHint = boundaryHint is not null ? $"{baseHint} {boundaryHint}" : baseHint;
            return new MethodQueryResult(
                methodDisplay,
                returnTypeDisplay,
                assertText,
                assumeText,
                because,
                QueryOutcomeStatus.Refuted,
                false,
                outcome.Obligations.Count,
                lastHash,
                methodLoc,
                coreReasons,
                coreTraceSteps,
                counterexamples,
                null,
                fullHint);
        }

        if (overall == QueryVerdict.Inconclusive)
        {
            return new MethodQueryResult(
                methodDisplay,
                returnTypeDisplay,
                assertText,
                assumeText,
                because,
                QueryOutcomeStatus.Inconclusive,
                false,
                outcome.Obligations.Count,
                lastHash,
                methodLoc,
                coreReasons,
                coreTraceSteps,
                Array.Empty<PathCounterexample>(),
                unknownMsg,
                "Try simplifying non-linear expressions, raising `--timeout`, or adding helper `Spec.LoopInvariant` annotations.");
        }

        var vacuous = overall == QueryVerdict.ProvedVacuously;
        var hint = vacuous
            ? "Warning: the premises / `--assume` condition cannot be satisfied on any path, so the claim holds only vacuously."
            : null;

        return new MethodQueryResult(
            methodDisplay,
            returnTypeDisplay,
            assertText,
            assumeText,
            because,
            QueryOutcomeStatus.Proved,
            vacuous,
            outcome.Obligations.Count,
            lastHash,
            methodLoc,
            coreReasons,
            coreTraceSteps,
            Array.Empty<PathCounterexample>(),
            null,
            hint);
    }

    /// <summary>
    /// The unsat-core query for one return path: the premises, each path condition and the result assumption
    /// as labelled assertions, then the labelled negated goal last. The assertion text comes from the kernel.
    /// </summary>
    internal static string BuildNamedQueryScript(QueryObligation ob, Dictionary<string, NamedPremise> premiseMap)
    {
        var sb = new StringBuilder(ob.ScriptPreamble.Length + 512);
        sb.Append(ob.ScriptPreamble).Append('\n');

        var (retFile, retLine) = GetLocationInfo(ob.ReturnLocation);
        for (var i = 0; i < ob.PathConjuncts.Count; i++)
        {
            var conj = ob.PathConjuncts[i];
            // No lowerer name starts with "path_", so this label cannot clash with a parameter (p_...) or local.
            var pathId = $"path_cond_{i + 1}";
            premiseMap[pathId] = new NamedPremise(
                pathId,
                "PathCondition",
                $"branch condition reaching return ({retFile}:{retLine})",
                null,
                retFile,
                retLine,
                conj);
            sb.Append(SolverPlumbingKernel.NamedAssertion(conj, pathId));
        }

        if (ob.ResultAssumption is { } ra)
        {
            premiseMap[ra.Id] = ra;
            sb.Append(SolverPlumbingKernel.NamedAssertion(ra.SmtFormula, ra.Id));
        }

        sb.Append(ProofKernel.NamedNegatedGoal(ob.GoalTerm));
        return sb.ToString();
    }

    private static bool TryBuildCfg(
        CSharpCompilation compilation,
        IMethodSymbol method,
        out ControlFlowGraph? cfg,
        out System.Collections.Immutable.ImmutableArray<IOperation> blocks,
        out string? error)
    {
        cfg = null;
        blocks = default;
        error = null;

        var syn = method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        if (syn is not MethodDeclarationSyntax md)
        {
            error = $"Target `{method.Name}` has no C# method syntax body to inspect.";
            return false;
        }

        var model = compilation.GetSemanticModel(md.SyntaxTree);
        var op = model.GetOperation(md);
        if (op is null)
        {
            error = $"Failed to obtain Roslyn operation tree for `{method.ContainingType?.Name}.{method.Name}`.";
            return false;
        }

        cfg = op switch
        {
            IMethodBodyOperation mbo => ControlFlowGraph.Create(mbo),
            IBlockOperation blk => ControlFlowGraph.Create(blk),
            _ => null
        };

        if (cfg is null)
        {
            error = $"Could not build control-flow graph for `{method.ContainingType?.Name}.{method.Name}`.";
            return false;
        }

        blocks = [op];
        return true;
    }

    private static List<IMethodSymbol> CollectQueryableMethods(CSharpCompilation compilation)
    {
        var list = new List<IMethodSymbol>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            if (IsGeneratedTree(tree.FilePath)) continue;
            var model = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();
            foreach (var md in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(md) is IMethodSymbol m && IsQueryableUserMethod(m))
                {
                    list.Add(m);
                }
            }
        }
        return list;
    }

    private static bool IsQueryableUserMethod(IMethodSymbol m)
    {
        if (m.MethodKind != MethodKind.Ordinary || m.ReturnsVoid || m.IsAbstract) return false;
        if (VerifiableSymbolHelpers.HasAttribute(m, VerifiableSymbolHelpers.PureAxiomAttr)) return false;
        if (VerifiableSymbolHelpers.GetScope(m) != VerificationScope.Verifiable) return false;
        if (VerifiableSymbolHelpers.HasAttribute(m, VerifiableSymbolHelpers.InvariantAttr)) return false;
        return m.DeclaringSyntaxReferences.Any(sr => sr.GetSyntax() is MethodDeclarationSyntax md && (md.Body is not null || md.ExpressionBody is not null));
    }

    private static List<IMethodSymbol> ResolveTargets(
        List<IMethodSymbol> allMethods,
        string targetName,
        out string? error,
        out string? fix)
    {
        error = null;
        fix = null;

        var exact = allMethods.Where(m =>
        {
            var shortName = $"{m.ContainingType?.Name}.{m.Name}";
            var fullName = m.ContainingNamespace is { IsGlobalNamespace: false } ns
                ? $"{ns.ToDisplayString()}.{shortName}"
                : shortName;
            return shortName.Equals(targetName, StringComparison.OrdinalIgnoreCase) ||
                   fullName.Equals(targetName, StringComparison.OrdinalIgnoreCase) ||
                   m.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase);
        }).ToList();

        if (exact.Count > 0)
        {
            return exact;
        }

        // Check if targetName matches a containing type (sweep all methods on that class)
        var byType = allMethods.Where(m =>
            m.ContainingType is not null &&
            (m.ContainingType.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase) ||
             m.ContainingType.ToDisplayString().Equals(targetName, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (byType.Count > 0)
        {
            return byType;
        }

        var candidates = allMethods
            .Select(m => $"{m.ContainingType?.Name}.{m.Name}")
            .Concat(allMethods.Where(m => m.ContainingType is not null).Select(m => m.ContainingType!.Name))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var suggestions = ElmSuggest.Rank(targetName, candidates, maxResults: 3);
        var didYouMean = ElmSuggest.FormatDidYouMean(suggestions);

        var sampleNames = string.Join(", ", allMethods.Take(6).Select(m => $"{m.ContainingType?.Name}.{m.Name}"));
        error = $"No `[Verifiable]` method or class named `{targetName}` was found.";
        if (didYouMean is not null)
        {
            fix = $"{didYouMean}\nRun `verifiable-query list` to see all verifiable targets.";
        }
        else
        {
            fix = allMethods.Count > 0
                ? $"Available methods include: {sampleNames}. Run `verifiable-query list` to see all targets."
                : "Ensure the target class or assembly is marked `[Verifiable]`.";
        }
        return new List<IMethodSymbol>();
    }

    private static INamedTypeSymbol? ResolveTypeSymbol(
        CSharpCompilation compilation,
        string typeName,
        out string? error,
        out string? fix)
    {
        error = null;
        fix = null;
        var matches = new List<INamedTypeSymbol>();
        var allTypeNames = new List<string>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            if (IsGeneratedTree(tree.FilePath)) continue;
            var model = compilation.GetSemanticModel(tree);
            foreach (var td in tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(td) is INamedTypeSymbol ts)
                {
                    allTypeNames.Add(ts.Name);
                    if (ts.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase) ||
                        ts.ToDisplayString().Equals(typeName, StringComparison.OrdinalIgnoreCase))
                    {
                        matches.Add(ts);
                    }
                }
            }
        }

        if (matches.Count == 1)
        {
            return matches[0];
        }

        if (matches.Count > 1)
        {
            var exact = matches.FirstOrDefault(m => m.ToDisplayString().Equals(typeName, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
            error = $"Multiple types matched `{typeName}`: {string.Join(", ", matches.Select(m => m.ToDisplayString()))}. Pass a namespace-qualified name.";
            return null;
        }

        var suggestions = ElmSuggest.Rank(typeName, allTypeNames, maxResults: 3);
        var didYouMean = ElmSuggest.FormatDidYouMean(suggestions);
        error = $"Type `{typeName}` was not found in the supplied sources.";
        fix = didYouMean is not null
            ? $"{didYouMean}\nRun `verifiable-query list` to inspect declared types."
            : "Check the type name with `verifiable-query list`.";
        return null;
    }

    private static bool TryBuildTypeInvariantProbe(
        IMethodSymbol method,
        INamedTypeSymbol targetType,
        string lambdaParam,
        ExpressionSyntax bodySyntax,
        out string? adaptedExpr)
    {
        adaptedExpr = null;
        var ret = method.ReturnType;

        if (SymbolEqualityComparer.Default.Equals(ret, targetType))
        {
            var replaced = QueryExpressionBinder.SubstituteParameter(bodySyntax, lambdaParam, SyntaxFactory.IdentifierName("result"));
            adaptedExpr = $"result => ({replaced})";
            return true;
        }

        if (ret is INamedTypeSymbol named)
        {
            if (named.OriginalDefinition.ToDisplayString() == "Verifiable.Contracts.Result<T, TError>" &&
                SymbolEqualityComparer.Default.Equals(named.TypeArguments[0], targetType))
            {
                var access = SyntaxFactory.ParseExpression("result.Value");
                var replaced = QueryExpressionBinder.SubstituteParameter(bodySyntax, lambdaParam, access);
                adaptedExpr = $"result => !result.IsSuccess || ({replaced})";
                return true;
            }

            var matchingProps = named.GetMembers().OfType<IPropertySymbol>()
                .Where(p => !p.IsStatic && !p.IsIndexer && SymbolEqualityComparer.Default.Equals(p.Type, targetType))
                .ToList();

            if (matchingProps.Count == 1)
            {
                var access = SyntaxFactory.ParseExpression($"result.{matchingProps[0].Name}");
                var replaced = QueryExpressionBinder.SubstituteParameter(bodySyntax, lambdaParam, access);
                adaptedExpr = $"result => ({replaced})";
                return true;
            }
        }

        return false;
    }

    private static (int RequiresCount, int EnsuresCount) CountMethodSpecs(MethodDeclarationSyntax md)
    {
        static bool IsSpec(InvocationExpressionSyntax inv) =>
            inv.Expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "Spec" } };

        static string Name(InvocationExpressionSyntax inv) =>
            ((MemberAccessExpressionSyntax)inv.Expression).Name.Identifier.ValueText;

        // Only a Requires in the leading contract block is a precondition; see LeadingContractBlock.
        var req = Verifiable.Shared.LeadingContractBlock.GetLeadingSpecCalls(md, IsSpec)
            .Count(inv => Name(inv) is "Requires" or "RequiresWhen");
        var ens = md.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Count(inv => IsSpec(inv) && Name(inv).StartsWith("Ensures", StringComparison.Ordinal));
        return (req, ens);
    }

    private static QueryReport BuildReport(
        string command,
        string claim,
        string? assume,
        string? because,
        string? targetType,
        IReadOnlyList<MethodQueryResult> results)
    {
        var status = QueryOutcomeStatus.Proved;
        if (results.Any(r => r.Status == QueryOutcomeStatus.Z3NotFound))
        {
            status = QueryOutcomeStatus.Z3NotFound;
        }
        else if (results.Any(r => r.Status == QueryOutcomeStatus.Refuted))
        {
            status = QueryOutcomeStatus.Refuted;
        }
        else if (results.Any(r => r.Status == QueryOutcomeStatus.Error))
        {
            status = QueryOutcomeStatus.Error;
        }
        else if (results.Any(r => r.Status == QueryOutcomeStatus.Inconclusive))
        {
            status = QueryOutcomeStatus.Inconclusive;
        }

        return new QueryReport(command, claim, assume, because, targetType, status, results, null, null, Location.None, null);
    }

    private static QueryReport ErrorReport(
        string command,
        string claim,
        string? assume,
        string? because,
        string? targetType,
        string title,
        string message,
        Location location,
        string? fix)
    {
        var status = title == "Z3 SOLVER NOT FOUND" ? QueryOutcomeStatus.Z3NotFound : QueryOutcomeStatus.Error;
        return new QueryReport(
            command,
            claim,
            assume,
            because,
            targetType,
            status,
            Array.Empty<MethodQueryResult>(),
            title,
            message,
            location,
            fix);
    }

    private static bool IsGeneratedTree(string? filePath) =>
        string.IsNullOrEmpty(filePath) ||
        filePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase);

    private static (string File, int Line) GetLocationInfo(Location loc)
    {
        if (loc == Location.None || !loc.IsInSource) return (string.Empty, 0);
        var span = loc.GetMappedLineSpan();
        var path = span.Path ?? loc.SourceTree?.FilePath ?? string.Empty;
        return (string.IsNullOrEmpty(path) ? string.Empty : Path.GetFileName(path), span.StartLinePosition.Line + 1);
    }
}
