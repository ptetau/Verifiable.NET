using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Verifiable.Analyzers;

/// <summary>
/// Path-sensitive CFG dataflow analyzer enforcing linear obligation tokens (<c>[MustConsume]</c> / <c>[ConsumesObligation]</c>)
/// across all methods in the compilation (VER007).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class LinearObligationAnalyzer : DiagnosticAnalyzer
{
    private enum ObligationState
    {
        Consumed = 0,
        Live = 1,
        MaybeLive = 2,
        SinkParameter = 3
    }

    private sealed record LinearDiag(
        Location Loc,
        Location? RelatedLoc,
        string? RelatedHeading,
        string Title,
        string TokenName,
        string TypeName,
        string Detail,
        string FixHint);

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        [VerifiableDescriptors.LinearObligationViolation];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterOperationBlockAction(AnalyzeOperationBlock);
    }

    private static void AnalyzeOperationBlock(OperationBlockAnalysisContext context)
    {
        var owning = context.OwningSymbol;
        if (VerifiableSymbolHelpers.IsSynthesizedRecordMember(owning) ||
            VerifiableSymbolHelpers.IsGeneratedByVerifiableGenerators(owning))
        {
            return;
        }

        if (!BlockMentionsMustConsume(owning, context.OperationBlocks))
        {
            return;
        }

        var reported = new HashSet<(int Start, int Length, string Message)>();

        foreach (var root in context.OperationBlocks)
        {
            if (root is IBlockOperation or IMethodBodyOperation or IConstructorBodyOperation)
            {
                var cfg = context.GetControlFlowGraph(root);
                AnalyzeCfgRecursive(context, owning, cfg, reported);
            }
        }
    }

    private static void AnalyzeCfgRecursive(
        OperationBlockAnalysisContext context,
        ISymbol methodSymbol,
        ControlFlowGraph cfg,
        HashSet<(int Start, int Length, string Message)> reported)
    {
        AnalyzeCfg(context, methodSymbol, cfg, reported);

        foreach (var localFn in cfg.LocalFunctions)
        {
            var localCfg = cfg.GetLocalFunctionControlFlowGraph(localFn);
            AnalyzeCfgRecursive(context, localFn, localCfg, reported);
        }

        foreach (var block in cfg.Blocks)
        {
            foreach (var op in block.Operations)
            {
                foreach (var flowAnon in op.DescendantsAndSelf().OfType<IFlowAnonymousFunctionOperation>())
                {
                    var anonCfg = cfg.GetAnonymousFunctionControlFlowGraph(flowAnon);
                    AnalyzeCfgRecursive(context, flowAnon.Symbol, anonCfg, reported);
                }
            }

            if (block.BranchValue is { } bv)
            {
                foreach (var flowAnon in bv.DescendantsAndSelf().OfType<IFlowAnonymousFunctionOperation>())
                {
                    var anonCfg = cfg.GetAnonymousFunctionControlFlowGraph(flowAnon);
                    AnalyzeCfgRecursive(context, flowAnon.Symbol, anonCfg, reported);
                }
            }
        }
    }

    private static bool BlockMentionsMustConsume(ISymbol owning, ImmutableArray<IOperation> blocks)
    {
        if (owning is IMethodSymbol method)
        {
            if (VerifiableSymbolHelpers.ContainsMustConsumeElement(method.ReturnType, out _))
            {
                return true;
            }
            if (method.Parameters.Any(p => VerifiableSymbolHelpers.ContainsMustConsumeElement(p.Type, out _)))
            {
                return true;
            }
        }

        foreach (var b in blocks)
        {
            foreach (var op in b.DescendantsAndSelf())
            {
                if (VerifiableSymbolHelpers.ContainsMustConsumeElement(op.Type, out _))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void AnalyzeCfg(
        OperationBlockAnalysisContext context,
        ISymbol methodSymbol,
        ControlFlowGraph cfg,
        HashSet<(int Start, int Length, string Message)> reported)
    {
        if (cfg.Blocks.Length == 0)
        {
            return;
        }

        var isSinkMethod = VerifiableSymbolHelpers.HasAttribute(methodSymbol, VerifiableSymbolHelpers.ConsumesObligationAttr);
        var entryState = ImmutableDictionary.CreateBuilder<ISymbol, ObligationState>(SymbolEqualityComparer.Default);
        if (methodSymbol is IMethodSymbol ms)
        {
            foreach (var p in ms.Parameters)
            {
                if (VerifiableSymbolHelpers.IsMustConsumeType(p.Type))
                {
                    entryState[p] = isSinkMethod ? ObligationState.SinkParameter : ObligationState.Live;
                }
            }
        }

        var inStates = new Dictionary<BasicBlock, ImmutableDictionary<ISymbol, ObligationState>>();
        var outStates = new Dictionary<BasicBlock, ImmutableDictionary<ISymbol, ObligationState>>();
        var consumedSites = new Dictionary<ISymbol, Location>(SymbolEqualityComparer.Default);
        var entryBlock = cfg.Blocks[0];
        inStates[entryBlock] = entryState.ToImmutable();

        var worklist = new Queue<BasicBlock>();
        worklist.Enqueue(entryBlock);

        // Phase 1: Fixed-point dataflow convergence
        var iterations = 0;
        const int maxIterations = 2000;
        while (worklist.Count > 0 && iterations++ < maxIterations)
        {
            var block = worklist.Dequeue();
            if (!inStates.TryGetValue(block, out var inState))
            {
                continue;
            }

            var state = inState;
            SimulateBlock(block, ref state, consumedSites, diagnostics: null);

            if (outStates.TryGetValue(block, out var prevOut) && DictionariesEqual(prevOut, state))
            {
                continue;
            }

            outStates[block] = state;

            foreach (var succ in GetSuccessors(block))
            {
                if (succ is null) continue;
                var merged = inStates.TryGetValue(succ, out var existingIn)
                    ? Join(existingIn, state)
                    : state;

                if (!inStates.TryGetValue(succ, out var oldIn) || !DictionariesEqual(oldIn, merged))
                {
                    inStates[succ] = merged;
                    worklist.Enqueue(succ);
                }
            }
        }

        // Phase 2: Final deterministic diagnostic emission on converged states
        foreach (var block in cfg.Blocks)
        {
            if (block.Kind == BasicBlockKind.Exit || !inStates.TryGetValue(block, out var inState))
            {
                continue;
            }

            var state = inState;
            var diags = new List<LinearDiag>();
            SimulateBlock(block, ref state, consumedSites, diags);

            // Check normal Return / exit branch
            CheckReturnBranchLeaks(block, state, diags);

            foreach (var d in diags)
            {
                var span = d.Loc.SourceSpan;
                if (reported.Add((span.Start, span.Length, d.Detail)))
                {
                    var elmMessage = ElmDiagnosticFormatter.Format(
                        d.Title,
                        d.Loc,
                        $"Linear obligation token `{d.TokenName}` (`{d.TypeName}`) {d.Detail}.",
                        spirit: "A `[MustConsume]` token represents work that must finish exactly once along every code path (such as emitting a command or committing state).",
                        fixHint: d.FixHint,
                        relatedLocation: d.RelatedLoc,
                        relatedHeading: d.RelatedHeading);
                    var additionalLocs = d.RelatedLoc is { IsInSource: true } rel && rel != Location.None
                        ? new[] { rel }
                        : null;
                    context.ReportDiagnostic(Diagnostic.Create(
                        VerifiableDescriptors.LinearObligationViolation,
                        d.Loc,
                        additionalLocations: additionalLocs,
                        elmMessage));
                }
            }
        }
    }

    private static IEnumerable<BasicBlock?> GetSuccessors(BasicBlock block)
    {
        if (block.FallThroughSuccessor?.Destination is { } f) yield return f;
        if (block.ConditionalSuccessor?.Destination is { } c) yield return c;
    }

    private static void SimulateBlock(
        BasicBlock block,
        ref ImmutableDictionary<ISymbol, ObligationState> state,
        Dictionary<ISymbol, Location> consumedSites,
        List<LinearDiag>? diagnostics)
    {
        var builder = state.ToBuilder();
        var consumedExpressions = new HashSet<IOperation>();
        var returnOwnedExpressions = new HashSet<IOperation>();

        foreach (var op in block.Operations)
        {
            ProcessTopLevelOperation(op, builder, consumedSites, consumedExpressions, returnOwnedExpressions, diagnostics);
        }

        if (block.BranchValue is { } branchVal)
        {
            var isReturnBranch = block.FallThroughSuccessor?.Semantics == ControlFlowBranchSemantics.Return
                && block.ConditionalSuccessor is null;
            if (isReturnBranch)
            {
                MarkReturnedOwnership(branchVal, builder, consumedSites, returnOwnedExpressions, diagnostics);
            }
            WalkNonAssigningReads(branchVal, builder, consumedSites, consumedExpressions, returnOwnedExpressions, diagnostics);
        }

        state = builder.ToImmutable();
    }

    private static bool IsNormalReturnOrExit(ControlFlowBranch? branch) =>
        branch is not null &&
        (branch.Semantics == ControlFlowBranchSemantics.Return ||
         (branch.Destination?.Kind == BasicBlockKind.Exit &&
          branch.Semantics == ControlFlowBranchSemantics.Regular));

    private static void CheckReturnBranchLeaks(
        BasicBlock block,
        ImmutableDictionary<ISymbol, ObligationState> postState,
        List<LinearDiag> diagnostics)
    {
        if (!IsNormalReturnOrExit(block.FallThroughSuccessor) &&
            !IsNormalReturnOrExit(block.ConditionalSuccessor))
        {
            return;
        }

        var returnLoc = block.Operations.LastOrDefault()?.Syntax.GetLocation()
            ?? block.BranchValue?.Syntax.GetLocation()
            ?? Location.None;

        foreach (var kv in postState)
        {
            if (kv.Value is ObligationState.Live or ObligationState.MaybeLive)
            {
                var isPartialBranch = kv.Value == ObligationState.MaybeLive;
                var detail = isPartialBranch
                    ? "may not be consumed on all execution paths before returning"
                    : "was never consumed before returning from this method";
                var fixHint = isPartialBranch
                    ? $"Consume `{kv.Key.Name}` on every branch (pass it to a `[ConsumesObligation]` method, return it, or call `Obligation.Discard({kv.Key.Name}, \"reason\")` on the unconsumed branch)."
                    : $"Pass `{kv.Key.Name}` to a `[ConsumesObligation]` method, return it to the caller, or explicitly call `Obligation.Discard({kv.Key.Name}, \"reason\")`.";
                var declLoc = kv.Key.Locations.FirstOrDefault() ?? returnLoc;
                var relatedLoc = returnLoc != Location.None && returnLoc.IsInSource ? returnLoc : null;
                var relatedHeading = relatedLoc is not null
                    ? "The method can exit here while the obligation is still live:"
                    : null;

                diagnostics.Add(new LinearDiag(
                    declLoc,
                    relatedLoc,
                    relatedHeading,
                    "UNCONSUMED OBLIGATION",
                    kv.Key.Name,
                    GetTypeName(kv.Key),
                    detail,
                    fixHint));
            }
        }
    }

    private static void ProcessTopLevelOperation(
        IOperation op,
        ImmutableDictionary<ISymbol, ObligationState>.Builder state,
        Dictionary<ISymbol, Location> consumedSites,
        HashSet<IOperation> consumedExpressions,
        HashSet<IOperation> returnOwnedExpressions,
        List<LinearDiag>? diagnostics)
    {
        // Walk children first (post-order for calls inside statements), except handle assignments/declarations directly
        switch (op)
        {
            case IExpressionStatementOperation exprStmt:
                ProcessExpressionValue(exprStmt.Operation, isRootStatement: true, state, consumedSites, consumedExpressions, returnOwnedExpressions, diagnostics);
                break;

            case IVariableDeclarationGroupOperation declGroup:
                foreach (var decl in declGroup.Declarations)
                {
                    foreach (var declarator in decl.Declarators)
                    {
                        HandleVariableDeclarator(declarator, state, consumedSites, consumedExpressions, returnOwnedExpressions, diagnostics);
                    }
                }
                break;

            case IVariableDeclarationOperation singleDecl:
                foreach (var declarator in singleDecl.Declarators)
                {
                    HandleVariableDeclarator(declarator, state, consumedSites, consumedExpressions, returnOwnedExpressions, diagnostics);
                }
                break;

            case IVariableDeclaratorOperation directDeclarator:
                HandleVariableDeclarator(directDeclarator, state, consumedSites, consumedExpressions, returnOwnedExpressions, diagnostics);
                break;

            case ISimpleAssignmentOperation simpleAssign:
                HandleSimpleAssignment(simpleAssign, state, consumedSites, consumedExpressions, returnOwnedExpressions, diagnostics);
                break;

            case IDeconstructionAssignmentOperation deconstructAssign:
                HandleDeconstructionAssignment(deconstructAssign, state, consumedSites, consumedExpressions, returnOwnedExpressions, diagnostics);
                break;

            case IReturnOperation retOp:
                if (retOp.ReturnedValue is { } retVal)
                {
                    MarkReturnedOwnership(retVal, state, consumedSites, returnOwnedExpressions, diagnostics);
                    WalkNonAssigningReads(retVal, state, consumedSites, consumedExpressions, returnOwnedExpressions, diagnostics);
                }
                break;

            default:
                WalkNonAssigningReads(op, state, consumedSites, consumedExpressions, returnOwnedExpressions, diagnostics);
                break;
        }
    }

    private static void ProcessExpressionValue(
        IOperation expr,
        bool isRootStatement,
        ImmutableDictionary<ISymbol, ObligationState>.Builder state,
        Dictionary<ISymbol, Location> consumedSites,
        HashSet<IOperation> consumedExpressions,
        HashSet<IOperation> returnOwnedExpressions,
        List<LinearDiag>? diagnostics)
    {
        WalkNonAssigningReads(expr, state, consumedSites, consumedExpressions, returnOwnedExpressions, diagnostics);

        if (isRootStatement && !expr.IsImplicit)
        {
            var unwrapped = UnwrapConversion(expr);
            if (!consumedExpressions.Contains(unwrapped) &&
                VerifiableSymbolHelpers.ContainsMustConsumeElement(unwrapped.Type, out var linearType))
            {
                if (unwrapped is not ILocalReferenceOperation && unwrapped is not IParameterReferenceOperation)
                {
                    diagnostics?.Add(new LinearDiag(
                        expr.Syntax.GetLocation(),
                        null,
                        null,
                        "DROPPED OBLIGATION",
                        "<temporary>",
                        linearType!.Name,
                        "was produced and immediately dropped without being stored or consumed",
                        "Bind the returned obligation to a local variable (`var token = ...;`) and pass it to a `[ConsumesObligation]` method, return it, or call `Obligation.Discard(...)`."));
                }
            }
        }
    }

    private static void HandleVariableDeclarator(
        IVariableDeclaratorOperation declarator,
        ImmutableDictionary<ISymbol, ObligationState>.Builder state,
        Dictionary<ISymbol, Location> consumedSites,
        HashSet<IOperation> consumedExpressions,
        HashSet<IOperation> returnOwnedExpressions,
        List<LinearDiag>? diagnostics)
    {
        var initVal = declarator.Initializer?.Value;
        if (initVal is not null)
        {
            WalkNonAssigningReads(initVal, state, consumedSites, consumedExpressions, returnOwnedExpressions, diagnostics, skipMovedRoot: true);
        }

        if (VerifiableSymbolHelpers.IsMustConsumeType(declarator.Symbol.Type))
        {
            if (initVal is not null)
            {
                AssignToLinearTarget(declarator.Symbol, initVal, declarator.Syntax.GetLocation(), state, consumedSites, consumedExpressions, diagnostics);
            }
        }
    }

    private static void HandleSimpleAssignment(
        ISimpleAssignmentOperation assign,
        ImmutableDictionary<ISymbol, ObligationState>.Builder state,
        Dictionary<ISymbol, Location> consumedSites,
        HashSet<IOperation> consumedExpressions,
        HashSet<IOperation> returnOwnedExpressions,
        List<LinearDiag>? diagnostics)
    {
        WalkNonAssigningReads(assign.Value, state, consumedSites, consumedExpressions, returnOwnedExpressions, diagnostics, skipMovedRoot: true);

        var target = UnwrapConversion(assign.Target);
        if (target is IDiscardOperation)
        {
            var rhs = UnwrapConversion(assign.Value);
            if (!consumedExpressions.Contains(rhs) &&
                VerifiableSymbolHelpers.ContainsMustConsumeElement(rhs.Type, out var linearType))
            {
                if (TryGetTrackedSymbol(rhs, out var movedSym) && state.TryGetValue(movedSym!, out var curSt))
                {
                    if (curSt is ObligationState.Live or ObligationState.MaybeLive)
                    {
                        var originLoc = movedSym!.Locations.FirstOrDefault();
                        diagnostics?.Add(new LinearDiag(
                            assign.Syntax.GetLocation(),
                            originLoc,
                            originLoc is { IsInSource: true } ? "This obligation was created or received here:" : null,
                            "UNCONSUMED OBLIGATION",
                            movedSym.Name,
                            linearType!.Name,
                            "was explicitly discarded with `_` without being consumed",
                            $"Pass `{movedSym.Name}` to a `[ConsumesObligation]` method, or document intentional cancellation with `Obligation.Discard({movedSym.Name}, \"reason\")`."));
                        state[movedSym] = ObligationState.Consumed;
                    }
                }
                else
                {
                    diagnostics?.Add(new LinearDiag(
                        assign.Syntax.GetLocation(),
                        null,
                        null,
                        "DROPPED OBLIGATION",
                        "_",
                        linearType!.Name,
                        "was explicitly discarded with `_` without being consumed",
                        "Store the obligation in a named variable and pass it to a `[ConsumesObligation]` method, or call `Obligation.Discard(..., \"reason\")`."));
                }
            }
            return;
        }

        if (target is ILocalReferenceOperation localRef && VerifiableSymbolHelpers.IsMustConsumeType(localRef.Local.Type))
        {
            AssignToLinearTarget(localRef.Local, assign.Value, assign.Syntax.GetLocation(), state, consumedSites, consumedExpressions, diagnostics);
        }
        else if (target is IParameterReferenceOperation paramRef && VerifiableSymbolHelpers.IsMustConsumeType(paramRef.Parameter.Type))
        {
            AssignToLinearTarget(paramRef.Parameter, assign.Value, assign.Syntax.GetLocation(), state, consumedSites, consumedExpressions, diagnostics);
        }
    }

    private static void HandleDeconstructionAssignment(
        IDeconstructionAssignmentOperation deconstruct,
        ImmutableDictionary<ISymbol, ObligationState>.Builder state,
        Dictionary<ISymbol, Location> consumedSites,
        HashSet<IOperation> consumedExpressions,
        HashSet<IOperation> returnOwnedExpressions,
        List<LinearDiag>? diagnostics)
    {
        WalkNonAssigningReads(deconstruct.Value, state, consumedSites, consumedExpressions, returnOwnedExpressions, diagnostics, skipMovedRoot: true);

        if (deconstruct.Target is ITupleOperation tupleTarget)
        {
            var rhsTupleElems = UnwrapConversion(deconstruct.Value) is ITupleOperation rhsTuple
                ? rhsTuple.Elements
                : ImmutableArray<IOperation>.Empty;

            for (var i = 0; i < tupleTarget.Elements.Length; i++)
            {
                var elemTarget = UnwrapConversion(tupleTarget.Elements[i]);
                ISymbol? targetSym = elemTarget switch
                {
                    ILocalReferenceOperation lr => lr.Local,
                    IDeclarationExpressionOperation de when de.Expression is ILocalReferenceOperation dlr => dlr.Local,
                    _ => null
                };

                if (targetSym is not null && VerifiableSymbolHelpers.IsMustConsumeType(GetSymbolType(targetSym)))
                {
                    if (i < rhsTupleElems.Length)
                    {
                        AssignToLinearTarget(targetSym, rhsTupleElems[i], elemTarget.Syntax.GetLocation(), state, consumedSites, consumedExpressions, diagnostics);
                    }
                    else
                    {
                        AssignFreshLinearTarget(targetSym, elemTarget.Syntax.GetLocation(), state, diagnostics);
                    }
                }
            }
        }
    }

    private static void AssignToLinearTarget(
        ISymbol targetSym,
        IOperation rhs,
        Location assignLoc,
        ImmutableDictionary<ISymbol, ObligationState>.Builder state,
        Dictionary<ISymbol, Location> consumedSites,
        HashSet<IOperation> consumedExpressions,
        List<LinearDiag>? diagnostics)
    {
        var unwrappedRhs = UnwrapConversion(rhs);
        if (TryGetTrackedSymbol(unwrappedRhs, out var srcSym))
        {
            if (SymbolEqualityComparer.Default.Equals(srcSym, targetSym))
            {
                return;
            }

            if (state.TryGetValue(srcSym!, out var srcState))
            {
                if (srcState == ObligationState.Consumed)
                {
                    consumedSites.TryGetValue(srcSym!, out var priorLoc);
                    diagnostics?.Add(new LinearDiag(
                        rhs.Syntax.GetLocation(),
                        priorLoc,
                        priorLoc is { IsInSource: true } ? "This obligation was already consumed or moved here:" : null,
                        "OBLIGATION ALREADY CONSUMED",
                        srcSym!.Name,
                        GetTypeName(srcSym!),
                        "was already consumed before assignment",
                        $"Remove the duplicate use of `{srcSym.Name}`, or create a fresh `{GetTypeName(srcSym)}` token."));
                }
                else if (srcState != ObligationState.SinkParameter)
                {
                    state[srcSym!] = ObligationState.Consumed;
                    if (!consumedSites.ContainsKey(srcSym!))
                    {
                        consumedSites[srcSym!] = rhs.Syntax.GetLocation();
                    }
                }
            }
        }
        else
        {
            consumedExpressions.Add(unwrappedRhs);
        }

        AssignFreshLinearTarget(targetSym, assignLoc, state, diagnostics);
    }

    private static void AssignFreshLinearTarget(
        ISymbol targetSym,
        Location assignLoc,
        ImmutableDictionary<ISymbol, ObligationState>.Builder state,
        List<LinearDiag>? diagnostics)
    {
        if (state.TryGetValue(targetSym, out var existing) && existing is ObligationState.Live or ObligationState.MaybeLive)
        {
            var prevLoc = targetSym.Locations.FirstOrDefault();
            diagnostics?.Add(new LinearDiag(
                assignLoc,
                prevLoc,
                prevLoc is { IsInSource: true } ? "The previous obligation was bound here and never consumed:" : null,
                "OBLIGATION OVERWRITTEN",
                targetSym.Name,
                GetTypeName(targetSym),
                "was overwritten before its previous obligation was consumed",
                $"Consume or discard the previous `{targetSym.Name}` before reassigning it."));
        }

        state[targetSym] = ObligationState.Live;
    }

    private static void WalkNonAssigningReads(
        IOperation op,
        ImmutableDictionary<ISymbol, ObligationState>.Builder state,
        Dictionary<ISymbol, Location> consumedSites,
        HashSet<IOperation> consumedExpressions,
        HashSet<IOperation> returnOwnedExpressions,
        List<LinearDiag>? diagnostics,
        bool skipMovedRoot = false)
    {
        // Note: handle assignments/declarators embedded inside other expressions (e.g., nested assignment)
        switch (op)
        {
            case IVariableDeclaratorOperation v:
                HandleVariableDeclarator(v, state, consumedSites, consumedExpressions, returnOwnedExpressions, diagnostics);
                return;
            case ISimpleAssignmentOperation sa:
                HandleSimpleAssignment(sa, state, consumedSites, consumedExpressions, returnOwnedExpressions, diagnostics);
                return;
            case IDeconstructionAssignmentOperation da:
                HandleDeconstructionAssignment(da, state, consumedSites, consumedExpressions, returnOwnedExpressions, diagnostics);
                return;
            case IBlockOperation:
                return; // nested blocks/lambdas have their own CFG
        }

        if (op is IInvocationOperation inv)
        {
            var consumesArgs = VerifiableSymbolHelpers.HasAttribute(inv.TargetMethod, VerifiableSymbolHelpers.ConsumesObligationAttr);
            if (inv.Instance is not null)
            {
                WalkNonAssigningReads(inv.Instance, state, consumedSites, consumedExpressions, returnOwnedExpressions, diagnostics);
            }

            foreach (var arg in inv.Arguments)
            {
                var argVal = UnwrapConversion(arg.Value);
                if (consumesArgs && VerifiableSymbolHelpers.IsMustConsumeType(argVal.Type))
                {
                    if (TryGetTrackedSymbol(argVal, out var argSym))
                    {
                        if (state.TryGetValue(argSym!, out var cur))
                        {
                            if (cur == ObligationState.Consumed)
                            {
                                consumedSites.TryGetValue(argSym!, out var priorLoc);
                                diagnostics?.Add(new LinearDiag(
                                    arg.Syntax.GetLocation(),
                                    priorLoc,
                                    priorLoc is { IsInSource: true } ? "It was already consumed here:" : null,
                                    "OBLIGATION ALREADY CONSUMED",
                                    argSym!.Name,
                                    GetTypeName(argSym!),
                                    "was consumed more than once",
                                    $"Remove this duplicate consumption of `{argSym.Name}` so the obligation is discharged exactly once."));
                            }
                            else if (cur != ObligationState.SinkParameter)
                            {
                                state[argSym!] = ObligationState.Consumed;
                                if (!consumedSites.ContainsKey(argSym!))
                                {
                                    consumedSites[argSym!] = arg.Syntax.GetLocation();
                                }
                            }
                        }
                        else
                        {
                            diagnostics?.Add(new LinearDiag(
                                arg.Syntax.GetLocation(),
                                null,
                                null,
                                "UNCONSUMED OBLIGATION",
                                argSym!.Name,
                                GetTypeName(argSym!),
                                "was consumed before being initialized",
                                $"Initialize `{argSym.Name}` from a producer before passing it to a `[ConsumesObligation]` method."));
                        }
                    }
                    else
                    {
                        WalkNonAssigningReads(argVal, state, consumedSites, consumedExpressions, returnOwnedExpressions, diagnostics, skipMovedRoot: true);
                        consumedExpressions.Add(argVal);
                    }
                }
                else
                {
                    WalkNonAssigningReads(arg.Value, state, consumedSites, consumedExpressions, returnOwnedExpressions, diagnostics);
                }
            }
            return;
        }

        if (!skipMovedRoot && TryGetTrackedSymbol(op, out var sym) && VerifiableSymbolHelpers.IsMustConsumeType(GetSymbolType(sym!)))
        {
            var unwrappedOp = UnwrapConversion(op);
            if (!returnOwnedExpressions.Contains(unwrappedOp) &&
                !consumedExpressions.Contains(unwrappedOp) &&
                state.TryGetValue(sym!, out var cur) &&
                cur == ObligationState.Consumed)
            {
                consumedSites.TryGetValue(sym!, out var priorLoc);
                diagnostics?.Add(new LinearDiag(
                    op.Syntax.GetLocation(),
                    priorLoc,
                    priorLoc is { IsInSource: true } ? "It was already consumed or moved here:" : null,
                    "OBLIGATION ALREADY CONSUMED",
                    sym!.Name,
                    GetTypeName(sym!),
                    "was used after being consumed or moved",
                    $"Read any fields you need from `{sym.Name}` before passing it to a `[ConsumesObligation]` method or moving it."));
            }
            return;
        }

        // If a nested sub-expression produces a [MustConsume] value inside a non-returning, non-assigning, non-consuming expression
        foreach (var child in op.ChildOperations)
        {
            var childSkip = skipMovedRoot && UnwrapConversion(op) == UnwrapConversion(child);
            WalkNonAssigningReads(child, state, consumedSites, consumedExpressions, returnOwnedExpressions, diagnostics, childSkip);
        }
    }

    private static void MarkReturnedOwnership(
        IOperation retVal,
        ImmutableDictionary<ISymbol, ObligationState>.Builder state,
        Dictionary<ISymbol, Location> consumedSites,
        HashSet<IOperation> returnOwnedExpressions,
        List<LinearDiag>? diagnostics)
    {
        var unwrapped = UnwrapConversion(retVal);
        if (TryGetTrackedSymbol(unwrapped, out var directSym) && VerifiableSymbolHelpers.IsMustConsumeType(GetSymbolType(directSym!)))
        {
            ConsumeForReturn(directSym!, unwrapped, state, consumedSites, returnOwnedExpressions, diagnostics);
            return;
        }

        // Support returning wrapped obligations inside tuples, Result<T, E>.Ok(token), or immutable record constructors
        if (unwrapped is ITupleOperation tuple)
        {
            foreach (var elem in tuple.Elements)
            {
                MarkReturnedOwnership(elem, state, consumedSites, returnOwnedExpressions, diagnostics);
            }
        }
        else if (unwrapped is IInvocationOperation inv &&
                 VerifiableSymbolHelpers.HasAttribute(inv.TargetMethod, VerifiableSymbolHelpers.PureAxiomAttr))
        {
            foreach (var arg in inv.Arguments)
            {
                MarkReturnedOwnership(arg.Value, state, consumedSites, returnOwnedExpressions, diagnostics);
            }
        }
        else if (unwrapped is IObjectCreationOperation obj)
        {
            foreach (var arg in obj.Arguments)
            {
                MarkReturnedOwnership(arg.Value, state, consumedSites, returnOwnedExpressions, diagnostics);
            }
        }
    }

    private static void ConsumeForReturn(
        ISymbol sym,
        IOperation expr,
        ImmutableDictionary<ISymbol, ObligationState>.Builder state,
        Dictionary<ISymbol, Location> consumedSites,
        HashSet<IOperation> returnOwnedExpressions,
        List<LinearDiag>? diagnostics)
    {
        if (state.TryGetValue(sym, out var cur))
        {
            if (cur == ObligationState.Consumed)
            {
                consumedSites.TryGetValue(sym, out var priorLoc);
                diagnostics?.Add(new LinearDiag(
                    expr.Syntax.GetLocation(),
                    priorLoc,
                    priorLoc is { IsInSource: true } ? "It was already consumed here:" : null,
                    "OBLIGATION ALREADY CONSUMED",
                    sym.Name,
                    GetTypeName(sym),
                    "was already consumed before being returned",
                    $"Either return `{sym.Name}` to transfer ownership to the caller, or consume it locally—not both."));
            }
            else if (cur != ObligationState.SinkParameter)
            {
                state[sym] = ObligationState.Consumed;
                if (!consumedSites.ContainsKey(sym))
                {
                    consumedSites[sym] = expr.Syntax.GetLocation();
                }
            }
        }
        returnOwnedExpressions.Add(expr);
    }

    private static IOperation UnwrapConversion(IOperation op)
    {
        while (op is IConversionOperation conv)
        {
            op = conv.Operand;
        }
        return op;
    }

    private static bool TryGetTrackedSymbol(IOperation op, out ISymbol? symbol)
    {
        op = UnwrapConversion(op);
        switch (op)
        {
            case ILocalReferenceOperation lr:
                symbol = lr.Local;
                return true;
            case IParameterReferenceOperation pr:
                symbol = pr.Parameter;
                return true;
            default:
                symbol = null;
                return false;
        }
    }

    private static ITypeSymbol GetSymbolType(ISymbol symbol) => symbol switch
    {
        ILocalSymbol l => l.Type,
        IParameterSymbol p => p.Type,
        _ => throw new InvalidOperationException($"Unexpected symbol kind {symbol.Kind}")
    };

    private static string GetTypeName(ISymbol symbol) =>
        GetSymbolType(symbol).Name;

    private static ImmutableDictionary<ISymbol, ObligationState> Join(
        ImmutableDictionary<ISymbol, ObligationState> a,
        ImmutableDictionary<ISymbol, ObligationState> b)
    {
        var result = ImmutableDictionary.CreateBuilder<ISymbol, ObligationState>(SymbolEqualityComparer.Default);
        var keys = new HashSet<ISymbol>(a.Keys, SymbolEqualityComparer.Default);
        keys.UnionWith(b.Keys);

        foreach (var key in keys)
        {
            var hasA = a.TryGetValue(key, out var stA);
            var hasB = b.TryGetValue(key, out var stB);

            if (hasA && hasB)
            {
                if (stA == stB)
                {
                    result[key] = stA;
                }
                else if (stA == ObligationState.SinkParameter || stB == ObligationState.SinkParameter)
                {
                    result[key] = ObligationState.SinkParameter;
                }
                else
                {
                    result[key] = ObligationState.MaybeLive;
                }
            }
            else
            {
                var present = hasA ? stA : stB;
                result[key] = present == ObligationState.Consumed
                    ? ObligationState.Consumed
                    : (present == ObligationState.SinkParameter ? ObligationState.SinkParameter : ObligationState.MaybeLive);
            }
        }

        return result.ToImmutable();
    }

    private static bool DictionariesEqual(
        ImmutableDictionary<ISymbol, ObligationState> a,
        ImmutableDictionary<ISymbol, ObligationState> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var kv in a)
        {
            if (!b.TryGetValue(kv.Key, out var val) || val != kv.Value)
            {
                return false;
            }
        }
        return true;
    }
}
