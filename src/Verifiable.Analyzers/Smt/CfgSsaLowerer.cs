using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Verifiable.Analyzers.Smt;

internal sealed record ProofObligation(
    string Kind,
    string ContractText,
    Location Location,
    string PathCondition,
    string GoalTerm,
    string ScriptPreamble,
    IReadOnlyList<(string ParamName, string SmtName)> TrackedParameters,
    string? Reason = null);

internal sealed record NamedPremise(
    string Id,
    string Kind,
    string Label,
    string? Reason,
    string? FilePath,
    int Line,
    string SmtFormula);

internal sealed record QueryObligation(
    string MethodDisplay,
    string ClaimText,
    Location ReturnLocation,
    string PathCondition,
    IReadOnlyList<string> PathConjuncts,
    NamedPremise? ResultAssumption,
    string GoalTerm,
    string ResultSmtName,
    string ScriptPreamble,
    IReadOnlyList<(string ParamName, string SmtName)> TrackedParameters,
    IReadOnlyList<NamedPremise> Premises);

internal sealed record QueryLoweringOutcome(
    IReadOnlyList<QueryObligation> Obligations,
    IReadOnlyList<ProofObligation> Preconditions,
    IReadOnlyList<(string Contract, Location Location, string Error)> Errors);

internal sealed record LoweringOutcome(
    IReadOnlyList<ProofObligation> Obligations,
    IReadOnlyList<(string Contract, Location Location, string Error)> Errors);

internal sealed partial class CfgSsaLowerer
{
    private readonly Compilation _compilation;
    private readonly IMethodSymbol _method;
    private readonly bool _trackProofPremises;
    private readonly bool _closedEnums;
    private readonly SmtTypeSystem _types;
    private readonly ContractExpressionTranslator _translator;

    private readonly List<string> _decls = new();
    private readonly List<string> _assertions = new();
    private readonly List<ProofObligation> _obligations = new();
    private readonly List<NamedPremise> _premises = new();
    private readonly List<QueryObligation> _queryObligations = new();
    private readonly List<(string Contract, Location Location, string Error)> _errors = new();
    private readonly List<(string ParamName, string SmtName)> _trackedParams = new();
    private readonly HashSet<string> _emittedOnDemandKeys = new(StringComparer.Ordinal);
    private readonly HashSet<IMethodSymbol> _expandingCalls = new(SymbolEqualityComparer.Default);

    private int _ssaCounter;
    private int _premiseCounter;

    private IOperation? _queryAssertOp;
    private IParameterSymbol? _queryResultParam;
    private IParameterSymbol? _queryThisParam;
    private IReadOnlyList<IParameterSymbol>? _queryProbeParams;
    private string? _queryAssertDisplay;
    private IOperation? _queryAssumeOp;
    private bool _queryAssumeUsesResult;
    private string? _queryAssumeDisplay;

    private sealed record EnsuresSpec(
        IParameterSymbol? LambdaParam,
        IOperation BodyExpr,
        Location Location,
        string DisplayText,
        string? Because = null);

    private sealed record LoopSpecGroup(
        ILoopOperation Loop,
        List<(IOperation Condition, Location Location, string DisplayText, string? Because)> LoopInvariants,
        List<(IOperation Measure, Location Location, string DisplayText, string? Because)> Decreases);

    private sealed class MethodSpecs
    {
        public HashSet<SyntaxNode> AnnotationCalls { get; } = new();
        public List<(IOperation Condition, Location Location, string DisplayText, string? Because)> Requires { get; } = new();
        public List<EnsuresSpec> Ensures { get; } = new();
        public List<LoopSpecGroup> Loops { get; } = new();
        public List<(string Contract, Location Location, string Error)> Errors { get; } = new();
    }

    private sealed class BlockEnv
    {
        public Dictionary<ISymbol, SmtTypedTerm> Vars { get; } = new(SymbolEqualityComparer.Default);
        public Dictionary<CaptureId, SmtTypedTerm> Captures { get; } = new();
        public Dictionary<CaptureId, ISymbol> CaptureAliases { get; } = new();
        public SmtTypedTerm? ThisOverride { get; set; }
        public SmtTypedTerm? SpecResult { get; set; }

        public BlockEnv Clone()
        {
            var copy = new BlockEnv
            {
                ThisOverride = ThisOverride,
                SpecResult = SpecResult
            };
            foreach (var kv in Vars) copy.Vars[kv.Key] = kv.Value;
            foreach (var kv in Captures) copy.Captures[kv.Key] = kv.Value;
            foreach (var kv in CaptureAliases) copy.CaptureAliases[kv.Key] = kv.Value;
            return copy;
        }
    }

    private sealed record IncomingEdge(BasicBlock From, string ReachCond, BlockEnv Env);

    private MethodSpecs _currentSpecs = new();
    private readonly Dictionary<ISymbol, SmtTypedTerm> _entryParamVars = new(SymbolEqualityComparer.Default);
    private SmtTypedTerm? _entryThis;

    public CfgSsaLowerer(Compilation compilation, IMethodSymbol method, bool trackProofPremises = false, bool closedEnums = false)
    {
        _compilation = compilation;
        _method = method;
        _trackProofPremises = trackProofPremises;
        _closedEnums = closedEnums;
        var useBitVec = HasCheckedArithmetic(method);
        _types = new SmtTypeSystem(useBitVec);
        _translator = new ContractExpressionTranslator(compilation, _types);
    }

    public QueryLoweringOutcome LowerWithQuery(
        ControlFlowGraph cfg,
        ImmutableArray<IOperation> rawOperationBlocks,
        IOperation assertBodyOp,
        IParameterSymbol? resultParam,
        IParameterSymbol? thisParam,
        IReadOnlyList<IParameterSymbol> probeParams,
        string assertDisplay,
        IOperation? assumeBodyOp = null,
        bool assumeUsesResult = false,
        string? assumeDisplay = null)
    {
        _queryObligations.Clear();
        _queryAssertOp = assertBodyOp;
        _queryResultParam = resultParam;
        _queryThisParam = thisParam;
        _queryProbeParams = probeParams;
        _queryAssertDisplay = assertDisplay;
        _queryAssumeOp = assumeBodyOp;
        _queryAssumeUsesResult = assumeUsesResult;
        _queryAssumeDisplay = assumeDisplay;

        Lower(cfg, rawOperationBlocks);

        _queryAssertOp = null;
        _queryAssumeOp = null;
        return new QueryLoweringOutcome(_queryObligations.ToList(),
            _obligations.Where(ob => ob.Kind == "Precondition").ToList(), _errors.ToList());
    }

    public LoweringOutcome Lower(ControlFlowGraph cfg, ImmutableArray<IOperation> rawOperationBlocks)
    {
        try
        {
            return LowerCore(cfg, rawOperationBlocks);
        }
        catch (ContractLoweringException ex)
        {
            // Body and CFG expressions can be unsupported just like specification predicates.
            _errors.Add((_method.Name, _method.Locations.FirstOrDefault() ?? Location.None, ex.Message));
            return new LoweringOutcome(_obligations, _errors);
        }
    }

    private LoweringOutcome LowerCore(ControlFlowGraph cfg, ImmutableArray<IOperation> rawOperationBlocks)
    {
        var methodLoc = _method.Locations.FirstOrDefault() ?? Location.None;
        var entryEnv = new BlockEnv();
        _entryParamVars.Clear();
        _entryThis = null;

        if (!_method.IsStatic && _method.ContainingType is { } recvType)
        {
            var sort = _types.GetSort(recvType);
            const string thisSmt = "p_this";
            _decls.Add($"(declare-const {thisSmt} {sort})");
            _trackedParams.Add(("this", thisSmt));
            var thisTerm = new SmtTypedTerm(thisSmt, recvType);
            _entryThis = thisTerm;
            entryEnv.ThisOverride = thisTerm;
            EmitTypeAssumptions(thisTerm, methodLoc);
            if (_method.MethodKind != MethodKind.Constructor)
                AssumeEntryEnumDomains(thisTerm, methodLoc);
        }

        foreach (var p in _method.Parameters)
        {
            var sort = _types.GetSort(p.Type);
            var smtName = "p_" + SmtTypeSystem.SanitizeName(p.Name);
            _decls.Add($"(declare-const {smtName} {sort})");
            _trackedParams.Add((p.Name, smtName));
            var pTerm = new SmtTypedTerm(smtName, p.Type);
            entryEnv.Vars[p] = pTerm;
            _entryParamVars[p] = pTerm;
            EmitTypeAssumptions(pTerm, p.Locations.FirstOrDefault() ?? methodLoc);
            AssumeEntryEnumDomains(pTerm, p.Locations.FirstOrDefault() ?? methodLoc);
        }

        _currentSpecs = ExtractMethodSpecs(_compilation, _method, rawOperationBlocks);
        foreach (var err in _currentSpecs.Errors)
        {
            _errors.Add(err);
        }

        // Method Spec.Requires entry assumptions
        foreach (var req in _currentSpecs.Requires)
        {
            try
            {
                var reqTerm = LowerExpression(req.Condition, "true", entryEnv, emitObligations: false);
                AddAssertion(reqTerm.Term, "Requires", req.DisplayText, req.Location, req.Because);
            }
            catch (ContractLoweringException ex)
            {
                _errors.Add((req.DisplayText, req.Location, ex.Message));
            }
        }

        if (_queryAssumeOp is not null && !_queryAssumeUsesResult)
        {
            try
            {
                var assumeEnv = BuildQueryEnv(entryEnv, resultTerm: null);
                var assumeTerm = LowerExpression(_queryAssumeOp, "true", assumeEnv, emitObligations: false);
                AddAssertion(assumeTerm.Term, "QueryAssumption", _queryAssumeDisplay ?? "assumption", methodLoc);
            }
            catch (ContractLoweringException ex)
            {
                _errors.Add((_queryAssumeDisplay ?? "assumption", methodLoc, ex.Message));
            }
        }

        if (_errors.Count > 0 || cfg.Blocks.Length == 0)
        {
            return new LoweringOutcome(_obligations, _errors);
        }

        var backEdges = FindBackEdges(cfg);
        var loopHeaders = new Dictionary<BasicBlock, List<BasicBlock>>();
        foreach (var (src, dst) in backEdges)
        {
            if (!loopHeaders.TryGetValue(dst, out var srcs))
            {
                srcs = new List<BasicBlock>();
                loopHeaders[dst] = srcs;
            }
            srcs.Add(src);
        }

        var loopBlocksByHeader = new Dictionary<BasicBlock, HashSet<BasicBlock>>();
        var innerLoopBlocks = new HashSet<BasicBlock>();
        foreach (var kv in loopHeaders)
        {
            var body = CollectNaturalLoopBlocks(kv.Key, kv.Value, cfg);
            loopBlocksByHeader[kv.Key] = body;
            foreach (var b in body)
            {
                if (!ReferenceEquals(b, kv.Key))
                {
                    innerLoopBlocks.Add(b);
                }
            }
        }

        var rpo = ComputeRpoIgnoringBackEdges(cfg, backEdges);
        var incoming = new Dictionary<BasicBlock, List<IncomingEdge>>();
        foreach (var b in cfg.Blocks) incoming[b] = new List<IncomingEdge>();

        incoming[cfg.Blocks[0]].Add(new IncomingEdge(cfg.Blocks[0], "true", entryEnv));

        foreach (var block in rpo)
        {
            if (block.Kind == BasicBlockKind.Exit || innerLoopBlocks.Contains(block))
            {
                continue;
            }

            var edges = incoming[block];
            if (edges.Count == 0)
            {
                continue;
            }

            if (loopBlocksByHeader.TryGetValue(block, out var loopBody))
            {
                LowerNaturalLoop(block, loopBody, edges, backEdges, incoming);
                continue;
            }

            var (gamma, env) = MergeIncomingEdges(edges);
            LowerBasicBlock(block, gamma, env, incoming);
        }

        return new LoweringOutcome(_obligations, _errors);
    }

    public static bool MethodHasSpecRequires(Compilation compilation, IMethodSymbol method)
    {
        foreach (var synRef in method.DeclaringSyntaxReferences)
        {
            if (synRef.GetSyntax() is MethodDeclarationSyntax md)
            {
                foreach (var inv in md.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var name = inv.Expression is MemberAccessExpressionSyntax access ? access.Name : inv.Expression;
                    if (name is IdentifierNameSyntax { Identifier.ValueText: "Requires" } &&
                        compilation.ContainsSyntaxTree(inv.SyntaxTree) &&
                        compilation.GetSemanticModel(inv.SyntaxTree).GetSymbolInfo(inv).Symbol is IMethodSymbol target &&
                        VerifiableSymbolHelpers.IsSpecCall(target, "Requires"))
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    private static MethodSpecs ExtractMethodSpecs(
        Compilation compilation,
        IMethodSymbol method,
        ImmutableArray<IOperation> existingBlocks)
    {
        var specs = new MethodSpecs();
        var roots = new List<IOperation>();

        if (!existingBlocks.IsDefaultOrEmpty)
        {
            roots.AddRange(existingBlocks);
        }
        else
        {
            foreach (var synRef in method.DeclaringSyntaxReferences)
            {
                var syn = synRef.GetSyntax();
                if (!compilation.SyntaxTrees.Contains(syn.SyntaxTree))
                {
                    continue;
                }

                var model = compilation.GetSemanticModel(syn.SyntaxTree);
                if (model.GetOperation(syn) is { } bodyOp)
                {
                    roots.Add(bodyOp);
                }
            }
        }

        var loopGroups = new Dictionary<ILoopOperation, LoopSpecGroup>();

        void Walk(IOperation op, ILoopOperation? enclosingLoop)
        {
            if (op is ILocalFunctionOperation or IAnonymousFunctionOperation)
            {
                return;
            }

            if (op is ILoopOperation loopOp)
            {
                if (!loopGroups.ContainsKey(loopOp))
                {
                    var grp = new LoopSpecGroup(loopOp, new(), new());
                    loopGroups[loopOp] = grp;
                    specs.Loops.Add(grp);
                }

                foreach (var child in op.ChildOperations)
                {
                    Walk(child, loopOp);
                }
                return;
            }

            if (op is IInvocationOperation inv &&
                inv.TargetMethod.ContainingType?.ToDisplayString() == VerifiableSymbolHelpers.SpecTypeName)
            {
                var loc = inv.Syntax.GetLocation();
                var text = inv.Syntax.ToString();
                if (inv.TargetMethod.Name is "Ensures" or "Requires" or "LoopInvariant" or "Decreases")
                    specs.AnnotationCalls.Add(inv.Syntax);

                var because = inv.Arguments.Length >= 2
                    ? ExtractConstantStringArg(inv.Arguments[1].Value)
                    : null;

                switch (inv.TargetMethod.Name)
                {
                    case "Requires" when inv.Arguments.Length is 1 or 2:
                        specs.Requires.Add((inv.Arguments[0].Value, loc, text, because));
                        return;

                    case "Ensures" when inv.TargetMethod.TypeArguments.Length == 1 && inv.Arguments.Length is 1 or 2:
                    {
                        var declaredType = inv.TargetMethod.TypeArguments[0];
                        if (!SymbolEqualityComparer.Default.Equals(declaredType, method.ReturnType))
                        {
                            specs.Errors.Add((text, loc,
                                $"Spec.Ensures<{declaredType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}> type argument does not match method return type '{method.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}'."));
                            return;
                        }

                        if (!TryExtractLambdaPredicate(inv.Arguments[0].Value, out var lambdaParam, out var bodyExpr, out var lambdaError))
                        {
                            specs.Errors.Add((text, loc, lambdaError!));
                            return;
                        }

                        specs.Ensures.Add(new EnsuresSpec(lambdaParam, bodyExpr!, loc, text, because));
                        return;
                    }

                    case "Ensures" when inv.TargetMethod.TypeArguments.Length == 0 && inv.Arguments.Length is 1 or 2:
                        specs.Ensures.Add(new EnsuresSpec(null, inv.Arguments[0].Value, loc, text, because));
                        return;

                    case "LoopInvariant" when inv.Arguments.Length is 1 or 2:
                        if (enclosingLoop is null)
                        {
                            specs.Errors.Add((text, loc, "Spec.LoopInvariant(...) must be called inside a loop body."));
                            return;
                        }
                        loopGroups[enclosingLoop].LoopInvariants.Add((inv.Arguments[0].Value, loc, text, because));
                        return;

                    case "Decreases" when inv.Arguments.Length is 1 or 2:
                        if (enclosingLoop is null)
                        {
                            specs.Errors.Add((text, loc, "Spec.Decreases(...) must be called inside a loop body."));
                            return;
                        }
                        loopGroups[enclosingLoop].Decreases.Add((inv.Arguments[0].Value, loc, text, because));
                        return;
                }
            }

            foreach (var child in op.ChildOperations)
            {
                Walk(child, enclosingLoop);
            }
        }

        foreach (var root in roots)
        {
            Walk(root, enclosingLoop: null);
        }

        return specs;
    }

    private static bool TryExtractLambdaPredicate(
        IOperation argOp,
        out IParameterSymbol? param,
        out IOperation? bodyExpr,
        out string? error)
    {
        var cur = argOp;
        while (cur is IConversionOperation conv)
        {
            cur = conv.Operand;
        }

        if (cur is IDelegateCreationOperation del)
        {
            cur = del.Target;
        }

        if (cur is IAnonymousFunctionOperation anon &&
            anon.Symbol.Parameters.Length == 1 &&
            anon.Body is { Operations.Length: 1 } block &&
            block.Operations[0] is IReturnOperation { ReturnedValue: { } retExpr })
        {
            param = anon.Symbol.Parameters[0];
            bodyExpr = retExpr;
            error = null;
            return true;
        }

        param = null;
        bodyExpr = null;
        error = "Spec.Ensures<TResult> requires a single-expression lambda predicate such as 'result => ...'.";
        return false;
    }

    private LoopSpecGroup? FindLoopSpecGroup(BasicBlock header, HashSet<BasicBlock> loopBody)
    {
        var anchorSpan = header.BranchValue?.Syntax.Span
            ?? loopBody.SelectMany(b => b.Operations).FirstOrDefault()?.Syntax.Span;

        if (anchorSpan is null)
        {
            return null;
        }

        LoopSpecGroup? best = null;
        foreach (var grp in _currentSpecs.Loops)
        {
            var loopSpan = grp.Loop.Syntax.Span;
            if (loopSpan.Contains(anchorSpan.Value))
            {
                if (best is null || loopSpan.Length < best.Loop.Syntax.Span.Length)
                {
                    best = grp;
                }
            }
        }
        return best;
    }

    private void LowerNaturalLoop(
        BasicBlock header,
        HashSet<BasicBlock> loopBody,
        List<IncomingEdge> preEdges,
        HashSet<(BasicBlock Src, BasicBlock Dst)> backEdges,
        Dictionary<BasicBlock, List<IncomingEdge>> incoming)
    {
        var (preGamma, preEnv) = MergeIncomingEdges(preEdges);
        var headerLoc = header.BranchValue?.Syntax.GetLocation() ?? _method.Locations.FirstOrDefault() ?? Location.None;

        var loopSpecs = FindLoopSpecGroup(header, loopBody);
        var loopInvs = loopSpecs?.LoopInvariants ?? new List<(IOperation Condition, Location Location, string DisplayText, string? Because)>();
        var decreasesExprs = loopSpecs?.Decreases ?? new List<(IOperation Measure, Location Location, string DisplayText, string? Because)>();

        // 1. Check LoopInvariant entry and Decreases >= 0 at pre-loop state
        foreach (var (condOp, loc, display, because) in loopInvs)
        {
            try
            {
                var invTerm = LowerExpression(condOp, preGamma, preEnv, emitObligations: false);
                AddObligation("LoopInvariantEntry", $"{display} on entry", loc, preGamma, invTerm.Term, because);
            }
            catch (ContractLoweringException ex)
            {
                _errors.Add((display, loc, ex.Message));
            }
        }

        foreach (var (decOp, loc, display, because) in decreasesExprs)
        {
            try
            {
                var decTerm = LowerExpression(decOp, preGamma, preEnv, emitObligations: false);
                var zero = _translator.FormatIntLiteral(0, _types.GetSort(decTerm.Type) == "(_ BitVec 64)" ? 64 : 32);
                var geOp = ContractExpressionTranslator.IsBitVecSort(_types.GetSort(decTerm.Type)) ? "bvsge" : ">=";
                AddObligation("LoopDecreasesEntry", $"{display} >= 0 on entry", loc, preGamma, $"({geOp} {decTerm.Term} {zero})", because);
            }
            catch (ContractLoweringException ex)
            {
                _errors.Add((display, loc, ex.Message));
            }
        }

        // 2. Havoc loop-modified variables
        var modifiedVars = CollectModifiedSymbols(loopBody);
        var loopEnv = preEnv.Clone();
        foreach (var sym in modifiedVars)
        {
            var type = GetSymbolType(sym);
            var smtVar = BindSsaValue("loop_" + SmtTypeSystem.SanitizeName(sym.Name), new SmtTypedTerm(_types.GetDefaultTerm(type), type), isFreshFreeSymbol: true);
            loopEnv.Vars[sym] = smtVar;
            EmitTypeAssumptions(smtVar, headerLoc);
        }

        var invAssumptions = new List<string>();
        foreach (var (condOp, loc, display, _) in loopInvs)
        {
            try
            {
                var invTerm = LowerExpression(condOp, preGamma, loopEnv, emitObligations: false);
                invAssumptions.Add(invTerm.Term);
            }
            catch (ContractLoweringException ex)
            {
                _errors.Add((display, loc, ex.Message));
            }
        }

        var decBeforeTerms = new List<(IOperation MeasureOp, Location Loc, string Display, string? Because, SmtTypedTerm Term)>();
        foreach (var (decOp, loc, display, because) in decreasesExprs)
        {
            try
            {
                var decTerm = LowerExpression(decOp, preGamma, loopEnv, emitObligations: false);
                var zero = _translator.FormatIntLiteral(0, _types.GetSort(decTerm.Type) == "(_ BitVec 64)" ? 64 : 32);
                var geOp = ContractExpressionTranslator.IsBitVecSort(_types.GetSort(decTerm.Type)) ? "bvsge" : ">=";
                invAssumptions.Add($"({geOp} {decTerm.Term} {zero})");
                decBeforeTerms.Add((decOp, loc, display, because, decTerm));
            }
            catch (ContractLoweringException ex)
            {
                _errors.Add((display, loc, ex.Message));
            }
        }

        var loopGamma = preGamma;
        foreach (var a in invAssumptions)
        {
            loopGamma = CombineAnd(loopGamma, a);
        }

        // Simulate header condition in loopEnv
        var headerWorkEnv = loopEnv.Clone();
        foreach (var op in header.Operations)
        {
            LowerOperation(op, loopGamma, headerWorkEnv);
        }

        var condDest = header.ConditionalSuccessor?.Destination;
        var fallDest = header.FallThroughSuccessor?.Destination;
        string? condBranchCond = null;
        string? fallBranchCond = null;

        if (header.BranchValue is { } bv && header.ConditionalSuccessor is not null && !IsAnnotationFragment(bv))
        {
            var condVal = LowerExpression(bv, loopGamma, headerWorkEnv);
            if (header.ConditionKind == ControlFlowConditionKind.WhenTrue)
            {
                condBranchCond = condVal.Term;
                fallBranchCond = $"(not {condVal.Term})";
            }
            else if (header.ConditionKind == ControlFlowConditionKind.WhenFalse)
            {
                condBranchCond = $"(not {condVal.Term})";
                fallBranchCond = condVal.Term;
            }
        }

        var bodyIncoming = new Dictionary<BasicBlock, List<IncomingEdge>>();
        foreach (var b in loopBody) bodyIncoming[b] = new List<IncomingEdge>();

        if (condDest is not null)
        {
            var edgeGamma = condBranchCond is not null ? CombineAnd(loopGamma, condBranchCond) : loopGamma;
            if (loopBody.Contains(condDest) && !ReferenceEquals(condDest, header))
                bodyIncoming[condDest].Add(new IncomingEdge(header, edgeGamma, headerWorkEnv.Clone()));
            else if (!ReferenceEquals(condDest, header))
                incoming[condDest].Add(new IncomingEdge(header, edgeGamma, headerWorkEnv.Clone()));
        }

        if (fallDest is not null && header.FallThroughSuccessor?.Semantics == ControlFlowBranchSemantics.Regular)
        {
            var edgeGamma = fallBranchCond is not null ? CombineAnd(loopGamma, fallBranchCond) : loopGamma;
            if (loopBody.Contains(fallDest) && !ReferenceEquals(fallDest, header))
                bodyIncoming[fallDest].Add(new IncomingEdge(header, edgeGamma, headerWorkEnv.Clone()));
            else if (!ReferenceEquals(fallDest, header))
                incoming[fallDest].Add(new IncomingEdge(header, edgeGamma, headerWorkEnv.Clone()));
        }

        var bodyOrder = ComputeSubgraphRpo(header, loopBody, backEdges);
        foreach (var b in bodyOrder)
        {
            if (ReferenceEquals(b, header)) continue;
            var bEdges = bodyIncoming[b];
            if (bEdges.Count == 0) continue;

            var (bGamma, bEnv) = MergeIncomingEdges(bEdges);

            foreach (var op in b.Operations)
            {
                LowerOperation(op, bGamma, bEnv);
            }

            if (b.ConditionalSuccessor?.Destination is { } bCondDest)
            {
                var bvTerm = b.BranchValue is { } branch && !IsAnnotationFragment(branch)
                    ? LowerExpression(branch, bGamma, bEnv) : null;
                string cTerm = "true", fTerm = "true";
                if (bvTerm is not null)
                {
                    if (b.ConditionKind == ControlFlowConditionKind.WhenTrue)
                    {
                        cTerm = bvTerm.Term;
                        fTerm = $"(not {bvTerm.Term})";
                    }
                    else if (b.ConditionKind == ControlFlowConditionKind.WhenFalse)
                    {
                        cTerm = $"(not {bvTerm.Term})";
                        fTerm = bvTerm.Term;
                    }
                }

                DispatchLoopBodyEdge(b, bCondDest, CombineAnd(bGamma, cTerm), bEnv, header, loopBody, bodyIncoming, incoming, loopInvs, decBeforeTerms);
                if (b.FallThroughSuccessor?.Destination is { } bFallDest)
                {
                    DispatchLoopBodyEdge(b, bFallDest, CombineAnd(bGamma, fTerm), bEnv, header, loopBody, bodyIncoming, incoming, loopInvs, decBeforeTerms);
                }
            }
            else if (b.FallThroughSuccessor?.Semantics == ControlFlowBranchSemantics.Return)
            {
                HandleReturnBranch(b, bGamma, bEnv);
            }
            else if (b.FallThroughSuccessor?.Destination is { } bFallOnly && b.FallThroughSuccessor.Semantics == ControlFlowBranchSemantics.Regular)
            {
                DispatchLoopBodyEdge(b, bFallOnly, bGamma, bEnv, header, loopBody, bodyIncoming, incoming, loopInvs, decBeforeTerms);
            }
        }
    }

    private void DispatchLoopBodyEdge(
        BasicBlock src,
        BasicBlock dst,
        string edgeGamma,
        BlockEnv env,
        BasicBlock header,
        HashSet<BasicBlock> loopBody,
        Dictionary<BasicBlock, List<IncomingEdge>> bodyIncoming,
        Dictionary<BasicBlock, List<IncomingEdge>> outerIncoming,
        List<(IOperation Condition, Location Location, string DisplayText, string? Because)> loopInvs,
        List<(IOperation MeasureOp, Location Loc, string Display, string? Because, SmtTypedTerm Term)> decBeforeTerms)
    {
        if (ReferenceEquals(dst, header))
        {
            foreach (var (condOp, loc, display, because) in loopInvs)
            {
                try
                {
                    var invTerm = LowerExpression(condOp, edgeGamma, env, emitObligations: false);
                    AddObligation("LoopInvariantStep", $"{display} preservation", loc, edgeGamma, invTerm.Term, because);
                }
                catch (ContractLoweringException ex)
                {
                    _errors.Add((display, loc, ex.Message));
                }
            }

            foreach (var (decOp, loc, display, because, beforeTerm) in decBeforeTerms)
            {
                try
                {
                    var afterTerm = LowerExpression(decOp, edgeGamma, env, emitObligations: false);
                    var zero = _translator.FormatIntLiteral(0, _types.GetSort(afterTerm.Type) == "(_ BitVec 64)" ? 64 : 32);
                    var isBv = ContractExpressionTranslator.IsBitVecSort(_types.GetSort(afterTerm.Type));
                    var geOp = isBv ? "bvsge" : ">=";
                    var ltOp = isBv ? "bvslt" : "<";
                    AddObligation("LoopDecreasesStep", $"{display} decreases", loc, edgeGamma,
                        $"(and ({geOp} {afterTerm.Term} {zero}) ({ltOp} {afterTerm.Term} {beforeTerm.Term}))", because);
                }
                catch (ContractLoweringException ex)
                {
                    _errors.Add((display, loc, ex.Message));
                }
            }
        }
        else if (loopBody.Contains(dst))
        {
            bodyIncoming[dst].Add(new IncomingEdge(src, edgeGamma, env.Clone()));
        }
        else if (dst.Kind != BasicBlockKind.Exit)
        {
            outerIncoming[dst].Add(new IncomingEdge(src, edgeGamma, env.Clone()));
        }
    }

    private void LowerBasicBlock(
        BasicBlock block,
        string gamma,
        BlockEnv env,
        Dictionary<BasicBlock, List<IncomingEdge>> incoming)
    {
        foreach (var op in block.Operations)
        {
            LowerOperation(op, gamma, env);
        }

        if (block.ConditionalSuccessor?.Destination is { } condDest)
        {
            string condTerm = "true", fallTerm = "true";
            if (block.BranchValue is { } bv && !IsAnnotationFragment(bv))
            {
                var c = LowerExpression(bv, gamma, env);
                if (block.ConditionKind == ControlFlowConditionKind.WhenTrue)
                {
                    condTerm = c.Term;
                    fallTerm = $"(not {c.Term})";
                }
                else if (block.ConditionKind == ControlFlowConditionKind.WhenFalse)
                {
                    condTerm = $"(not {c.Term})";
                    fallTerm = c.Term;
                }
            }

            if (condDest.Kind != BasicBlockKind.Exit)
            {
                incoming[condDest].Add(new IncomingEdge(block, CombineAnd(gamma, condTerm), env.Clone()));
            }

            if (block.FallThroughSuccessor?.Destination is { Kind: not BasicBlockKind.Exit } fallDest &&
                block.FallThroughSuccessor.Semantics == ControlFlowBranchSemantics.Regular)
            {
                incoming[fallDest].Add(new IncomingEdge(block, CombineAnd(gamma, fallTerm), env.Clone()));
            }
            return;
        }

        if (block.FallThroughSuccessor?.Semantics == ControlFlowBranchSemantics.Return)
        {
            HandleReturnBranch(block, gamma, env);
            return;
        }

        if (block.FallThroughSuccessor?.Destination is { Kind: not BasicBlockKind.Exit } next &&
            block.FallThroughSuccessor.Semantics == ControlFlowBranchSemantics.Regular)
        {
            incoming[next].Add(new IncomingEdge(block, gamma, env.Clone()));
        }
    }

    private void HandleReturnBranch(BasicBlock block, string gamma, BlockEnv env)
    {
        if (block.BranchValue is null)
        {
            return;
        }

        var retTerm = LowerExpression(block.BranchValue, gamma, env);
        var retLoc = block.BranchValue.Syntax.GetLocation();
        if (_queryAssertOp is not null)
        {
            RecordQueryObligation(retTerm, gamma, retLoc, env);
            return;
        }
        VerifyReturnSpecsAndInvariants(retTerm, gamma, retLoc, env);
    }

    private BlockEnv BuildQueryEnv(BlockEnv baseEnv, SmtTypedTerm? resultTerm)
    {
        var queryEnv = baseEnv.Clone();
        foreach (var kv in _entryParamVars)
        {
            queryEnv.Vars[kv.Key] = kv.Value;
        }
        if (_entryThis is not null)
        {
            queryEnv.ThisOverride = _entryThis;
            if (_queryThisParam is not null)
            {
                queryEnv.Vars[_queryThisParam] = _entryThis;
            }
        }
        if (_queryProbeParams is not null)
        {
            var resultName = _queryResultParam?.Name;
            foreach (var probeParam in _queryProbeParams)
            {
                if (resultName is not null && probeParam.Name == resultName)
                {
                    if (resultTerm is not null)
                    {
                        queryEnv.Vars[probeParam] = resultTerm;
                    }
                    continue;
                }

                if (_entryThis is not null && probeParam.Name == "__ver_this")
                {
                    queryEnv.Vars[probeParam] = _entryThis;
                    continue;
                }

                var matchingMethodParam = _method.Parameters.FirstOrDefault(p => p.Name == probeParam.Name);
                if (matchingMethodParam is not null && _entryParamVars.TryGetValue(matchingMethodParam, out var pTerm))
                {
                    queryEnv.Vars[probeParam] = pTerm;
                }
            }
        }
        if (resultTerm is not null)
        {
            if (_queryResultParam is not null)
            {
                queryEnv.Vars[_queryResultParam] = resultTerm;
            }
            queryEnv.SpecResult = resultTerm;
        }
        return queryEnv;
    }

    private void RecordQueryObligation(SmtTypedTerm retTerm, string gamma, Location retLoc, BlockEnv postEnv)
    {
        try
        {
            var boundRet = BindSsaValue("query_result", retTerm);
            var queryEnv = BuildQueryEnv(postEnv, boundRet);

            NamedPremise? resultAssumption = null;
            if (_queryAssumeOp is not null && _queryAssumeUsesResult)
            {
                var assumeTerm = LowerExpression(_queryAssumeOp, gamma, queryEnv, emitObligations: false);
                var assumeId = $"p_assume_result_{++_premiseCounter}";
                var (filePath, line) = ExtractFileAndLine(retLoc);
                resultAssumption = new NamedPremise(
                    assumeId,
                    "QueryAssumption",
                    _queryAssumeDisplay ?? "assumption",
                    null,
                    filePath,
                    line,
                    assumeTerm.Term);
            }

            var goalGamma = resultAssumption is null ? gamma : CombineAnd(gamma, resultAssumption.SmtFormula);
            var goal = LowerExpression(_queryAssertOp!, goalGamma, queryEnv, emitObligations: false);
            var tracked = _trackedParams.ToList();
            tracked.Add(("result", boundRet.Term));
            var conjuncts = FlattenConjuncts(gamma);
            var preamble = string.Join("\n", _types.Declarations.Concat(_decls).Concat(_assertions));
            var methodDisplay = $"{_method.ContainingType?.Name}.{_method.Name}";

            _queryObligations.Add(new QueryObligation(
                methodDisplay,
                _queryAssertDisplay ?? _queryAssertOp!.Syntax.ToString(),
                retLoc,
                gamma,
                conjuncts,
                resultAssumption,
                goal.Term,
                boundRet.Term,
                preamble,
                tracked,
                _premises.ToList()));
        }
        catch (ContractLoweringException ex)
        {
            _errors.Add((_queryAssertDisplay ?? "query", retLoc, ex.Message));
        }
    }

    private void VerifyReturnSpecsAndInvariants(SmtTypedTerm retTerm, string gamma, Location retLoc, BlockEnv postEnv)
    {
        foreach (var ens in _currentSpecs.Ensures)
        {
            try
            {
                var ensEnv = postEnv.Clone();
                foreach (var kv in _entryParamVars)
                {
                    ensEnv.Vars[kv.Key] = kv.Value;
                }
                if (_entryThis is not null)
                {
                    ensEnv.ThisOverride = _entryThis;
                }
                if (ens.LambdaParam is not null)
                {
                    ensEnv.Vars[ens.LambdaParam] = retTerm;
                }
                ensEnv.SpecResult = retTerm;

                var goal = LowerExpression(ens.BodyExpr, gamma, ensEnv, emitObligations: false);
                AddObligation("Postcondition", ens.DisplayText, retLoc, gamma, goal.Term, ens.Because);
            }
            catch (ContractLoweringException ex)
            {
                _errors.Add((ens.DisplayText, ens.Location, ex.Message));
            }
        }

        EmitReturnInvariantObligations(retTerm, gamma, retLoc);
    }

    private void EmitReturnInvariantObligations(SmtTypedTerm retTerm, string gamma, Location retLoc)
    {
        if (retTerm.Type is INamedTypeSymbol named &&
            named.OriginalDefinition.ToDisplayString() == "Verifiable.Contracts.Result<T, TError>" &&
            _types.TryGetComposite(named, out var resComp))
        {
            var isSuccessField = resComp!.Fields.First(f => f.Name == "IsSuccess");
            var valField = resComp.Fields.First(f => f.Name == "Value");
            var successCond = $"({isSuccessField.AccessorName} {retTerm.Term})";
            var valTerm = new SmtTypedTerm($"({valField.AccessorName} {retTerm.Term})", valField.Type);
            EmitValueInvariantObligations(valTerm, CombineAnd(gamma, successCond), retLoc);
            return;
        }

        EmitValueInvariantObligations(retTerm, gamma, retLoc);
    }

    private void EmitValueInvariantObligations(SmtTypedTerm value, string gamma, Location loc)
    {
        if (SmtTypeSystem.TryGetIsValidMethod(value.Type, out var isValidMethod) &&
            SmtTypeSystem.TryExtractSingleReturnOperation(_compilation, isValidMethod!, out var paramSym, out var isValidBody))
        {
            try
            {
                SmtTypeSystem.TryGetConstrainedUnderlying(value.Type, out _, out var typeDescription);
                var unwrapped = _translator.UnwrapRefinedIfApplicable(value);
                var valEnv = new BlockEnv();
                if (paramSym is not null) valEnv.Vars[paramSym] = unwrapped;
                var goal = LowerExpression(isValidBody!, gamma, valEnv, emitObligations: false);
                AddObligation("TypeInvariant", $"{value.Type.Name}.IsValid", loc, gamma, goal.Term, typeDescription);
            }
            catch (ContractLoweringException ex)
            {
                _errors.Add(($"{value.Type.Name}.IsValid", loc, ex.Message));
            }
            return;
        }

        foreach (var invMember in SmtTypeSystem.GetInvariantMembers(value.Type))
        {
            if (!SmtTypeSystem.TryExtractSingleReturnOperation(_compilation, invMember, out _, out var invBody))
            {
                _errors.Add(($"{value.Type.Name}.{invMember.Name}", loc,
                    $"[Invariant] member '{invMember.Name}' must be a single-expression method or property."));
                continue;
            }

            try
            {
                var invDescription = SmtTypeSystem.GetInvariantDescription(invMember);
                var invEnv = new BlockEnv { ThisOverride = value };
                var goal = LowerExpression(invBody!, gamma, invEnv, emitObligations: false);
                AddObligation("TypeInvariant", $"[Invariant] {value.Type.Name}.{invMember.Name}", loc, gamma, goal.Term, invDescription);
            }
            catch (ContractLoweringException ex)
            {
                _errors.Add(($"{value.Type.Name}.{invMember.Name}", loc, ex.Message));
            }
        }

        if (_types.TryGetComposite(value.Type, out var comp) && !SmtTypeSystem.TryGetConstrainedUnderlying(value.Type, out _))
        {
            foreach (var f in comp!.Fields)
            {
                if (SmtTypeSystem.HasAnyInvariants(f.Type))
                {
                    EmitValueInvariantObligations(new SmtTypedTerm($"({f.AccessorName} {value.Term})", f.Type), gamma, loc);
                }
            }
        }
    }

    // Roslyn splits short-circuit annotation arguments across CFG blocks. They are
    // checked from the original operation tree, not executed as body statements.
    // Both edges of an annotation-only condition remain reachable in body simulation.
    private bool IsAnnotationFragment(IOperation op) =>
        _currentSpecs.AnnotationCalls.Count != 0 &&
        op.Syntax.AncestorsAndSelf().Any(_currentSpecs.AnnotationCalls.Contains);

    private void LowerOperation(IOperation op, string gamma, BlockEnv env)
    {
        if (IsAnnotationFragment(op)) return;

        switch (op)
        {
            case IExpressionStatementOperation es:
                _ = LowerExpression(es.Operation, gamma, env);
                break;

            case IVariableDeclarationGroupOperation dg:
                foreach (var decl in dg.Declarations)
                {
                    foreach (var d in decl.Declarators)
                    {
                        LowerVariableDeclarator(d, gamma, env);
                    }
                }
                break;

            case IVariableDeclarationOperation sd:
                foreach (var d in sd.Declarators)
                {
                    LowerVariableDeclarator(d, gamma, env);
                }
                break;

            case IVariableDeclaratorOperation vd:
                LowerVariableDeclarator(vd, gamma, env);
                break;

            case IReturnOperation ret when ret.ReturnedValue is not null:
                var retVal = LowerExpression(ret.ReturnedValue, gamma, env);
                VerifyReturnSpecsAndInvariants(retVal, gamma, ret.Syntax.GetLocation(), env);
                break;

            default:
                _ = LowerExpression(op, gamma, env);
                break;
        }
    }

    private void LowerVariableDeclarator(IVariableDeclaratorOperation vd, string gamma, BlockEnv env)
    {
        if (vd.Initializer is not null)
        {
            var rhs = LowerExpression(vd.Initializer.Value, gamma, env);
            var converted = _translator.ConvertTo(rhs, vd.Symbol.Type);
            var ssa = BindSsaValue(vd.Symbol.Name, converted);
            env.Vars[vd.Symbol] = ssa;
        }
        else
        {
            env.Vars[vd.Symbol] = new SmtTypedTerm(_types.GetDefaultTerm(vd.Symbol.Type), vd.Symbol.Type);
        }
    }

    private SmtTypedTerm LowerExpression(
        IOperation op,
        string gamma,
        BlockEnv env,
        bool emitObligations = true,
        int inlineDepth = 0)
    {
        TrackDecimalPrecisionBoundary(op);
        switch (op)
        {
            case IConversionOperation conv:
                if (conv.Conversion.IsUserDefined &&
                    (CalendarArithmetic.CalendarType(conv.Type) != null || CalendarArithmetic.CalendarType(conv.Operand.Type) != null))
                    throw new ContractLoweringException($"Unsupported user-defined calendar conversion '{conv.Syntax}'.");
                if (conv.OperatorMethod is not null &&
                    (conv.Type?.TypeKind == TypeKind.Enum || conv.Operand.Type?.TypeKind == TypeKind.Enum))
                    throw new ContractLoweringException($"Unsupported user-defined enum conversion in '{conv.Syntax}'.");
                // In the CFG the ?? arms and their conversions are separate operations;
                // do not reinterpret unsupported conversions (e.g. boxing) as identity.
                var inCoalesce = conv.Syntax.AncestorsAndSelf().Any(s => s.IsKind(SyntaxKind.CoalesceExpression));
                if (inCoalesce && conv.Conversion.IsUserDefined)
                    throw new ContractLoweringException($"Unsupported user-defined conversion in '{conv.Syntax}' under ??.");
                if (conv.Operand is ILiteralOperation litNull && litNull.ConstantValue.HasValue && litNull.ConstantValue.Value is null)
                {
                    if (conv.Type is not null)
                    {
                        if (_types.TryGetNullableInfo(conv.Type, out _, out var optId, out _))
                            return new SmtTypedTerm($"opt-none-{optId}", conv.Type);
                        if (conv.Type.SpecialType == SpecialType.System_String)
                            return new SmtTypedTerm("str-null", conv.Type);
                        if (inCoalesce)
                            throw new ContractLoweringException($"No SMT null representation for '{conv.Type}'.");
                        return new SmtTypedTerm(_types.GetDefaultTerm(conv.Type), conv.Type);
                    }
                }
                var inner = LowerExpression(conv.Operand, gamma, env, emitObligations, inlineDepth);
                if (IsCheckedEnumCast(conv, _compilation) && conv.Type is { } checkedTarget &&
                    !_types.TryGetNullableInfo(checkedTarget, out _, out _, out _))
                {
                    var cast = _translator.ConvertTo(inner, checkedTarget);
                    AddObligation("Overflow", $"Checked enum cast '{conv.Syntax}' must fit {checkedTarget.Name}",
                        conv.Syntax.GetLocation(), gamma, ContractExpressionTranslator.FitsIntegralType(_translator.IntTermForIndex(inner), checkedTarget));
                    return cast;
                }
                if (conv.Type is { } target && !SymbolEqualityComparer.Default.Equals(inner.Type, target) &&
                    _types.TryGetNullableInfo(inner.Type, out _, out var srcId, out var srcType) &&
                    _types.TryGetNullableInfo(target, out _, out var dstId, out var dstType))
                {
                    var payload = new SmtTypedTerm($"(opt-val-{srcId} {inner.Term})", srcType!);
                    var converted = ConvertNullableValue(payload, dstType!);
                    return new SmtTypedTerm($"(ite (= {inner.Term} opt-none-{srcId}) opt-none-{dstId} (opt-some-{dstId} {converted.Term}))", target);
                }
                if (inCoalesce && conv.IsImplicit && conv.Type is not null)
                    return ConvertNullableValue(inner, conv.Type);
                return conv.Type is not null ? _translator.ConvertTo(inner, conv.Type) : inner;

            case IParenthesizedOperation paren:
                return LowerExpression(paren.Operand, gamma, env, emitObligations, inlineDepth);

            case ILocalReferenceOperation lr:
                if (env.Vars.TryGetValue(lr.Local, out var lVal)) return lVal;
                return new SmtTypedTerm(_types.GetDefaultTerm(lr.Local.Type), lr.Local.Type);

            case IParameterReferenceOperation pr:
                if (env.Vars.TryGetValue(pr.Parameter, out var pVal)) return pVal;
                return new SmtTypedTerm("p_" + SmtTypeSystem.SanitizeName(pr.Parameter.Name), pr.Parameter.Type);

            case IInstanceReferenceOperation inst when inst.ReferenceKind == InstanceReferenceKind.ContainingTypeInstance:
                if (env.ThisOverride is not null) return env.ThisOverride;
                if (_entryThis is not null) return _entryThis;
                return new SmtTypedTerm(_types.GetDefaultTerm(op.Type!), op.Type!);

            case IFlowCaptureOperation cap:
                var capVal = LowerExpression(cap.Value, gamma, env, emitObligations, inlineDepth);
                var capSsa = BindSsaValue("cap", capVal);
                env.Captures[cap.Id] = capSsa;
                if (cap.Value is ILocalReferenceOperation capLoc)
                    env.CaptureAliases[cap.Id] = capLoc.Local;
                else if (cap.Value is IParameterReferenceOperation capParam)
                    env.CaptureAliases[cap.Id] = capParam.Parameter;
                else
                    env.CaptureAliases.Remove(cap.Id);
                return capSsa;

            case IFlowCaptureReferenceOperation capRef:
                if (env.Captures.TryGetValue(capRef.Id, out var captured)) return captured;
                return new SmtTypedTerm(_types.GetDefaultTerm(capRef.Type!), capRef.Type!);

            case ILiteralOperation lit:
                return LowerLiteralOperation(lit);

            case IDefaultValueOperation def:
                return new SmtTypedTerm(_types.GetDefaultTerm(def.Type!), def.Type!);

            case ISimpleAssignmentOperation sa:
                var saRhs = LowerExpression(sa.Value, gamma, env, emitObligations, inlineDepth);
                AssignTarget(sa.Target, saRhs, env);
                return saRhs;

            case IDeconstructionAssignmentOperation da:
                var daRhs = LowerExpression(da.Value, gamma, env, emitObligations, inlineDepth);
                AssignDeconstruction(da.Target, daRhs, env);
                return daRhs;

            case ICompoundAssignmentOperation ca:
                var curTarget = LowerExpression(ca.Target, gamma, env, emitObligations, inlineDepth);
                var rhsVal = LowerExpression(ca.Value, gamma, env, emitObligations, inlineDepth);
                var combined = LowerBinaryOperator(ca.OperatorKind, curTarget, rhsVal, ca.Type!, gamma, ca.Syntax.GetLocation(), emitObligations);
                var combinedSsa = BindSsaValue("assign", combined);
                AssignTarget(ca.Target, combinedSsa, env);
                return combinedSsa;

            case IIncrementOrDecrementOperation inc:
                var prev = LowerExpression(inc.Target, gamma, env, emitObligations, inlineDepth);
                var one = new SmtTypedTerm(_translator.FormatIntLiteral(1, inc.Type!.SpecialType == SpecialType.System_Int64 ? 64 : 32), inc.Type);
                var binKind = inc.Kind == OperationKind.Increment ? BinaryOperatorKind.Add : BinaryOperatorKind.Subtract;
                var next = LowerBinaryOperator(binKind, prev, one, inc.Type, gamma, inc.Syntax.GetLocation(), emitObligations);
                var nextSsa = BindSsaValue("inc", next);
                AssignTarget(inc.Target, nextSsa, env);
                return inc.IsPostfix ? prev : nextSsa;

            case IUnaryOperation un:
                var uOp = LowerExpression(un.Operand, gamma, env, emitObligations, inlineDepth);
                if (CalendarArithmetic.CalendarType(uOp.Type) is not null)
                    return un.OperatorKind switch
                    {
                        UnaryOperatorKind.Plus => uOp,
                        UnaryOperatorKind.Minus => CheckedCalendar($"(- {uOp.Term})", uOp.Type, gamma, un.Syntax.GetLocation()),
                        _ => throw new ContractLoweringException($"Unsupported calendar operator '{un.Syntax}'.")
                    };
                if (uOp.Type.TypeKind == TypeKind.Enum)
                    throw new ContractLoweringException($"Unsupported enum operation '{un.OperatorKind}'.");
                if (un.OperatorKind == UnaryOperatorKind.Not)
                    return new SmtTypedTerm($"(not {uOp.Term})", _compilation.GetSpecialType(SpecialType.System_Boolean));
                if (un.OperatorKind == UnaryOperatorKind.Minus)
                {
                    uOp = _translator.UnwrapRefinedIfApplicable(uOp);
                    var sort = _types.GetSort(uOp.Type);
                    if (_types.UseBitVec && sort is "(_ BitVec 32)" or "(_ BitVec 64)")
                    {
                        if (emitObligations)
                        {
                            var minVal = sort == "(_ BitVec 32)" ? "(_ bv2147483648 32)" : "(_ bv9223372036854775808 64)";
                            AddObligation("Overflow", $"Unary negation overflow check on '{un.Syntax}'", un.Syntax.GetLocation(), gamma, $"(not (= {uOp.Term} {minVal}))");
                        }
                        return new SmtTypedTerm($"(bvneg {uOp.Term})", uOp.Type);
                    }
                    return new SmtTypedTerm($"(- {uOp.Term})", uOp.Type);
                }
                if (un.OperatorKind == UnaryOperatorKind.Plus)
                    return uOp;
                if (!emitObligations)
                    throw new ContractLoweringException($"Unsupported unary operator '{un.OperatorKind}' in specification.");
                return uOp;

            case IBinaryOperation bin:
                if (bin.OperatorKind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals &&
                    (ContainsUnmodeledReference(bin.LeftOperand.Type) || ContainsUnmodeledReference(bin.RightOperand.Type)))
                    throw new ContractLoweringException("Reference identity is not modeled for this type or its stored fields; compare individual properties instead.");
                if (TryCompareTotalSeconds(bin, gamma, env, emitObligations, inlineDepth) is { } seconds)
                    return seconds;
                if (bin.IsLifted)
                    return LowerLiftedComparison(bin, gamma, env, emitObligations, inlineDepth);
                if (bin.OperatorKind == BinaryOperatorKind.ConditionalAnd)
                {
                    var lAnd = LowerExpression(bin.LeftOperand, gamma, env, emitObligations, inlineDepth);
                    var rAnd = LowerExpression(bin.RightOperand, CombineAnd(gamma, lAnd.Term), env, emitObligations, inlineDepth);
                    return new SmtTypedTerm($"(and {lAnd.Term} {rAnd.Term})", _compilation.GetSpecialType(SpecialType.System_Boolean));
                }
                if (bin.OperatorKind == BinaryOperatorKind.ConditionalOr)
                {
                    var lOr = LowerExpression(bin.LeftOperand, gamma, env, emitObligations, inlineDepth);
                    var rOr = LowerExpression(bin.RightOperand, CombineAnd(gamma, $"(not {lOr.Term})"), env, emitObligations, inlineDepth);
                    return new SmtTypedTerm($"(or {lOr.Term} {rOr.Term})", _compilation.GetSpecialType(SpecialType.System_Boolean));
                }
                var lhs = LowerExpression(bin.LeftOperand, gamma, env, emitObligations, inlineDepth);
                var rhs = LowerExpression(bin.RightOperand, gamma, env, emitObligations, inlineDepth);
                if (bin.OperatorKind == BinaryOperatorKind.Subtract && lhs.Type.TypeKind == TypeKind.Enum &&
                    SymbolEqualityComparer.Default.Equals(lhs.Type, rhs.Type) && bin.Type is { TypeKind: not TypeKind.Enum } differenceType)
                {
                    var exact = $"(- {lhs.Term} {rhs.Term})";
                    var difference = _translator.ConvertTo(new SmtTypedTerm(exact, lhs.Type), differenceType);
                    if (bin.IsChecked || _types.UseBitVec)
                        AddObligation("Overflow", $"Checked enum subtraction '{bin.Syntax}'", bin.Syntax.GetLocation(),
                            gamma, ContractExpressionTranslator.FitsIntegralType(exact, differenceType));
                    return difference;
                }
                return LowerBinaryOperator(bin.OperatorKind, lhs, rhs, bin.Type!, gamma, bin.Syntax.GetLocation(), emitObligations);

            case IIsNullOperation isNull:
                var subject = LowerExpression(isNull.Operand, gamma, env, emitObligations, inlineDepth);
                return new SmtTypedTerm(NullTest(subject), _compilation.GetSpecialType(SpecialType.System_Boolean));

            case ICoalesceOperation coalesce:
                var value = LowerExpression(coalesce.Value, gamma, env, emitObligations, inlineDepth);
                var nullTest = NullTest(value);
                if (coalesce.ValueConversion.IsUserDefined)
                    throw new ContractLoweringException($"Unsupported user-defined ?? conversion in '{op.Syntax}'.");
                var present = value;
                if (_types.TryGetNullableInfo(value.Type, out _, out var id, out var payloadType) &&
                    !SymbolEqualityComparer.Default.Equals(value.Type, coalesce.Type))
                    present = new SmtTypedTerm($"(opt-val-{id} {value.Term})", payloadType!);
                present = ConvertNullableValue(present, coalesce.Type!);
                var fallback = LowerExpression(coalesce.WhenNull, CombineAnd(gamma, nullTest), env, emitObligations, inlineDepth);
                fallback = ConvertNullableValue(fallback, coalesce.Type!);
                return new SmtTypedTerm($"(ite {nullTest} {fallback.Term} {present.Term})", coalesce.Type!);

            case IConditionalOperation cond:
                var cTerm = LowerExpression(cond.Condition, gamma, env, emitObligations, inlineDepth);
                var tTerm = LowerExpression(cond.WhenTrue, CombineAnd(gamma, cTerm.Term), env, emitObligations, inlineDepth);
                var fTerm = LowerExpression(cond.WhenFalse!, CombineAnd(gamma, $"(not {cTerm.Term})"), env, emitObligations, inlineDepth);
                var (pt, pf, uType) = _translator.UnifyForComparison(tTerm, fTerm);
                return new SmtTypedTerm($"(ite {cTerm.Term} {pt} {pf})", uType);

            case IIsPatternOperation isPat:
                var subj = LowerExpression(isPat.Value, gamma, env, emitObligations, inlineDepth);
                return LowerPatternOp(subj, isPat.Pattern, gamma, env, emitObligations, inlineDepth);

            case IPropertyReferenceOperation propRef:
                return LowerPropertyReference(propRef, gamma, env, emitObligations, inlineDepth);

            case IFieldReferenceOperation fieldRef:
                return LowerFieldReference(fieldRef, gamma, env, emitObligations, inlineDepth);

            case IWithOperation withOp:
                return LowerWithOperation(withOp, gamma, env, emitObligations, inlineDepth);

            case IObjectCreationOperation objCreate:
                return LowerObjectCreation(objCreate, gamma, env, emitObligations, inlineDepth);

            case ITupleOperation tuple:
                return LowerTuple(tuple, gamma, env, emitObligations, inlineDepth);

            case IInvocationOperation inv:
                return LowerInvocation(inv, gamma, env, emitObligations, inlineDepth);

            case ICollectionExpressionOperation colExpr:
                return LowerCollectionExpression(colExpr, gamma, env, emitObligations, inlineDepth);

            default:
                if (!emitObligations || op.Type is not null)
                {
                    throw new ContractLoweringException($"Unsupported C# syntax '{op.Kind}' ('{op.Syntax}').");
                }
                return new SmtTypedTerm("0", _compilation.GetSpecialType(SpecialType.System_Int32));
        }
    }

    private string NullTest(SmtTypedTerm value)
    {
        if (_types.TryGetNullableInfo(value.Type, out _, out var id, out _))
            return $"(= {value.Term} opt-none-{id})";
        if (value.Type.SpecialType == SpecialType.System_String)
            return $"(= {value.Term} str-null)";
        throw new ContractLoweringException($"No SMT null representation for '{value.Type}'.");
    }

    private SmtTypedTerm ConvertNullableValue(SmtTypedTerm value, ITypeSymbol target)
    {
        if (SymbolEqualityComparer.Default.Equals(value.Type, target))
            return value;
        if (_types.TryGetNullableInfo(target, out _, out var id, out var payloadType))
        {
            var payload = ConvertNullableValue(value, payloadType!);
            return new SmtTypedTerm($"(opt-some-{id} {payload.Term})", target);
        }
        if ((value.Type.SpecialType, target.SpecialType) is
            (SpecialType.System_Int32, SpecialType.System_Int64 or SpecialType.System_Decimal) or
            (SpecialType.System_Int64, SpecialType.System_Decimal))
            return _translator.ConvertTo(value, target);
        throw new ContractLoweringException($"Unsupported nullable conversion from '{value.Type}' to '{target}'.");
    }

    private SmtTypedTerm LowerLiftedComparison(IBinaryOperation bin, string gamma, BlockEnv env, bool emitObligations, int inlineDepth)
    {
        var equality = bin.OperatorKind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals;
        if (bin.Type?.SpecialType != SpecialType.System_Boolean ||
            (!equality && bin.OperatorKind is not (BinaryOperatorKind.LessThan or BinaryOperatorKind.LessThanOrEqual or
                BinaryOperatorKind.GreaterThan or BinaryOperatorKind.GreaterThanOrEqual)) ||
            (bin.OperatorMethod is { } method && method.ContainingType.SpecialType != SpecialType.System_Decimal))
            throw new ContractLoweringException($"Unsupported lifted operator in '{bin.Syntax}'.");

        var left = LowerExpression(bin.LeftOperand, gamma, env, emitObligations, inlineDepth);
        var right = LowerExpression(bin.RightOperand, gamma, env, emitObligations, inlineDepth);
        if (!_types.TryGetNullableInfo(left.Type, out _, out var leftId, out var inner) ||
            !_types.TryGetNullableInfo(right.Type, out _, out var rightId, out _) || leftId != rightId ||
            (inner!.SpecialType is not (SpecialType.System_Int32 or SpecialType.System_Int64 or SpecialType.System_Decimal)
                && !(equality && (inner.SpecialType == SpecialType.System_Boolean || inner.TypeKind == TypeKind.Enum))))
            throw new ContractLoweringException($"Unsupported lifted operand types in '{bin.Syntax}'.");

        // Datatype equality already compares the presence tag as well as the payload.
        if (equality)
            return LowerBinaryOperator(bin.OperatorKind, left, right, bin.Type, gamma, bin.Syntax.GetLocation(), emitObligations);

        var cmp = LowerBinaryOperator(bin.OperatorKind,
            new SmtTypedTerm($"(opt-val-{leftId} {left.Term})", inner),
            new SmtTypedTerm($"(opt-val-{rightId} {right.Term})", inner), bin.Type, gamma, bin.Syntax.GetLocation(), emitObligations);
        return new SmtTypedTerm($"(and (not {NullTest(left)}) (not {NullTest(right)}) {cmp.Term})", bin.Type);
    }

    private SmtTypedTerm LowerLiteralOperation(ILiteralOperation lit)
    {
        var type = lit.Type ?? _compilation.GetSpecialType(SpecialType.System_String);
        if (!lit.ConstantValue.HasValue)
            return new SmtTypedTerm("str-null", type);

        var val = lit.ConstantValue.Value;
        if (val is null)
        {
            if (_types.TryGetNullableInfo(type, out _, out var optId, out _))
                return new SmtTypedTerm($"opt-none-{optId}", type);
            return new SmtTypedTerm("str-null", _compilation.GetSpecialType(SpecialType.System_String));
        }
        if (type.TypeKind == TypeKind.Enum)
            return new SmtTypedTerm(FormatEnumConstant(val), type);
        if (val is bool b)
            return new SmtTypedTerm(b ? "true" : "false", type);
        if (val is int iv)
            return new SmtTypedTerm(_translator.FormatIntLiteral(iv, 32), type);
        if (val is long lv)
            return new SmtTypedTerm(_translator.FormatLongLiteral(lv), type);
        if (val is decimal dv)
            return new SmtTypedTerm(ContractExpressionTranslator.FormatDecimalLiteral(dv), type);
        if (val is string sv)
            return new SmtTypedTerm($"(str-val {ContractExpressionTranslator.EscapeSmtString(sv)})", type);
        if (val is ulong ul)
            return new SmtTypedTerm(ul.ToString(CultureInfo.InvariantCulture), type);
        return new SmtTypedTerm(Convert.ToInt64(val, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture), type);
    }

    private SmtTypedTerm LowerBinaryOperator(
        BinaryOperatorKind kind,
        SmtTypedTerm left,
        SmtTypedTerm right,
        ITypeSymbol resultType,
        string gamma,
        Location loc,
        bool emitObligations = true)
    {
        if (CalendarArithmetic.CalendarType(left.Type) is not null || CalendarArithmetic.CalendarType(right.Type) is not null)
            return LowerCalendarBinary(kind, left, right, resultType, gamma, loc);

        if ((left.Type.TypeKind == TypeKind.Enum || right.Type.TypeKind == TypeKind.Enum) &&
            kind is not (BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals or
                BinaryOperatorKind.LessThan or BinaryOperatorKind.LessThanOrEqual or
                BinaryOperatorKind.GreaterThan or BinaryOperatorKind.GreaterThanOrEqual))
            throw new ContractLoweringException($"Unsupported enum operation '{kind}'.");

        if (kind == BinaryOperatorKind.Add &&
            (resultType.SpecialType == SpecialType.System_String ||
             left.Type.SpecialType == SpecialType.System_String ||
             right.Type.SpecialType == SpecialType.System_String))
        {
            var strType = _compilation.GetSpecialType(SpecialType.System_String);
            var strSsa = BindSsaValue("strcat", new SmtTypedTerm("str-null", strType), isFreshFreeSymbol: true);
            AddAssertion($"(not (= {strSsa.Term} str-null))", "StringAxiom", "concatenation non-null", loc);
            return strSsa;
        }

        if (kind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals)
        {
            var (lEq, rEq, _) = _translator.UnifyForComparison(left, right);
            var eq = $"(= {lEq} {rEq})";
            return new SmtTypedTerm(kind == BinaryOperatorKind.Equals ? eq : $"(not {eq})", _compilation.GetSpecialType(SpecialType.System_Boolean));
        }

        var (lTerm, rTerm, unified) = _translator.UnifyNumericPair(left, right);
        var sort = _types.GetSort(unified);
        var isBv = ContractExpressionTranslator.IsBitVecSort(sort);
        var boolType = _compilation.GetSpecialType(SpecialType.System_Boolean);

        if (emitObligations && isBv && kind is BinaryOperatorKind.Add or BinaryOperatorKind.Subtract or BinaryOperatorKind.Multiply)
        {
            EmitBitVecOverflowObligation(kind, lTerm, rTerm, sort, gamma, loc);
        }

        if (emitObligations && kind is BinaryOperatorKind.Divide or BinaryOperatorKind.Remainder && !isBv && sort != "Real")
        {
            AddObligation("DivisionByZero", "Divisor must be non-zero", loc, gamma, $"(not (= {rTerm} 0))");
        }
        else if (emitObligations && kind == BinaryOperatorKind.Divide && sort == "Real")
        {
            AddObligation("DivisionByZero", "Divisor must be non-zero", loc, gamma, $"(not (= {rTerm} 0.0))");
        }

        return kind switch
        {
            BinaryOperatorKind.LessThan => new SmtTypedTerm($"({(isBv ? "bvslt" : "<")} {lTerm} {rTerm})", boolType),
            BinaryOperatorKind.LessThanOrEqual => new SmtTypedTerm($"({(isBv ? "bvsle" : "<=")} {lTerm} {rTerm})", boolType),
            BinaryOperatorKind.GreaterThan => new SmtTypedTerm($"({(isBv ? "bvsgt" : ">")} {lTerm} {rTerm})", boolType),
            BinaryOperatorKind.GreaterThanOrEqual => new SmtTypedTerm($"({(isBv ? "bvsge" : ">=")} {lTerm} {rTerm})", boolType),
            BinaryOperatorKind.Add => new SmtTypedTerm($"({(isBv ? "bvadd" : "+")} {lTerm} {rTerm})", resultType),
            BinaryOperatorKind.Subtract => new SmtTypedTerm($"({(isBv ? "bvsub" : "-")} {lTerm} {rTerm})", resultType),
            BinaryOperatorKind.Multiply => new SmtTypedTerm($"({(isBv ? "bvmul" : "*")} {lTerm} {rTerm})", resultType),
            BinaryOperatorKind.Divide => new SmtTypedTerm($"({(isBv ? "bvsdiv" : (sort == "Real" ? "/" : "div"))} {lTerm} {rTerm})", resultType),
            BinaryOperatorKind.Remainder => new SmtTypedTerm($"({(isBv ? "bvsrem" : "mod")} {lTerm} {rTerm})", resultType),
            BinaryOperatorKind.And => new SmtTypedTerm($"(and {lTerm} {rTerm})", boolType),
            BinaryOperatorKind.Or => new SmtTypedTerm($"(or {lTerm} {rTerm})", boolType),
            _ when !emitObligations => throw new ContractLoweringException($"Unsupported binary operator '{kind}' in specification."),
            _ => BindSsaValue("bin", new SmtTypedTerm(_types.GetDefaultTerm(resultType), resultType), isFreshFreeSymbol: true)
        };
    }

    private void EmitBitVecOverflowObligation(BinaryOperatorKind kind, string lTerm, string rTerm, string sort, string gamma, Location loc)
    {
        var extBits = sort == "(_ BitVec 32)" ? 32 : 64;
        var extSortWidth = sort == "(_ BitVec 32)" ? 64 : 128;
        var op = kind switch
        {
            BinaryOperatorKind.Add => "bvadd",
            BinaryOperatorKind.Subtract => "bvsub",
            BinaryOperatorKind.Multiply => "bvmul",
            _ => "bvadd"
        };

        var extResult = $"({op} ((_ sign_extend {extBits}) {lTerm}) ((_ sign_extend {extBits}) {rTerm}))";
        string minBv, maxBv;
        if (extSortWidth == 64)
        {
            minBv = "(bvneg (_ bv2147483648 64))";
            maxBv = "(_ bv2147483647 64)";
        }
        else
        {
            minBv = "(bvneg (_ bv9223372036854775808 128))";
            maxBv = "(_ bv9223372036854775807 128)";
        }

        var goal = $"(and (bvsge {extResult} {minBv}) (bvsle {extResult} {maxBv}))";
        AddObligation("Overflow", $"Checked arithmetic overflow check ({kind})", loc, gamma, goal);
    }

    private SmtTypedTerm LowerPatternOp(
        SmtTypedTerm subject,
        IPatternOperation pattern,
        string gamma,
        BlockEnv env,
        bool emitObligations = true,
        int inlineDepth = 0)
    {
        var boolType = _compilation.GetSpecialType(SpecialType.System_Boolean);
        switch (pattern)
        {
            case IConstantPatternOperation cp:
                var val = LowerExpression(cp.Value, gamma, env, emitObligations, inlineDepth);
                var (l, r, _) = _translator.UnifyForComparison(subject, val);
                return new SmtTypedTerm($"(= {l} {r})", boolType);

            case INegatedPatternOperation np:
                var innerNeg = LowerPatternOp(subject, np.Pattern, gamma, env, emitObligations, inlineDepth);
                return new SmtTypedTerm($"(not {innerNeg.Term})", boolType);

            case IBinaryPatternOperation bp:
                var left = LowerPatternOp(subject, bp.LeftPattern, gamma, env, emitObligations, inlineDepth);
                var right = LowerPatternOp(subject, bp.RightPattern, gamma, env, emitObligations, inlineDepth);
                var op = bp.OperatorKind == BinaryOperatorKind.And ? "and" : "or";
                return new SmtTypedTerm($"({op} {left.Term} {right.Term})", boolType);

            case IRelationalPatternOperation rp:
                var rVal = LowerExpression(rp.Value, gamma, env, emitObligations, inlineDepth);
                return LowerBinaryOperator(rp.OperatorKind, subject, rVal, boolType, gamma, pattern.Syntax.GetLocation(), emitObligations);

            case IDeclarationPatternOperation dp:
                var notNull = subject.Type.SpecialType == SpecialType.System_String
                    ? $"(not (= {subject.Term} str-null))"
                    : (_types.TryGetNullableInfo(subject.Type, out _, out var optId, out _)
                        ? $"(not (= {subject.Term} opt-none-{optId}))"
                        : "true");
                if (dp.DeclaredSymbol is ILocalSymbol locSym)
                {
                    var bound = _types.TryGetNullableInfo(subject.Type, out _, out var oId, out var innerT)
                        ? new SmtTypedTerm($"(opt-val-{oId} {subject.Term})", innerT!)
                        : subject;
                    env.Vars[locSym] = bound;
                }
                return new SmtTypedTerm(notNull, boolType);

            case IRecursivePatternOperation recPat:
                return LowerRecursivePattern(subject, recPat, gamma, env, emitObligations, inlineDepth);

            case IDiscardPatternOperation:
                return new SmtTypedTerm("true", boolType);

            default:
                if (!emitObligations)
                    throw new ContractLoweringException($"Unsupported pattern '{pattern.Kind}' in specification.");
                return new SmtTypedTerm("true", boolType);
        }
    }

    private SmtTypedTerm LowerRecursivePattern(
        SmtTypedTerm subject,
        IRecursivePatternOperation recPat,
        string gamma,
        BlockEnv env,
        bool emitObligations,
        int inlineDepth)
    {
        var boolType = _compilation.GetSpecialType(SpecialType.System_Boolean);
        var clauses = new List<string>();
        var target = subject;

        if (subject.Type.SpecialType == SpecialType.System_String)
        {
            clauses.Add($"(not (= {subject.Term} str-null))");
        }
        else if (_types.TryGetNullableInfo(subject.Type, out _, out var optId, out var innerType))
        {
            clauses.Add($"(not (= {subject.Term} opt-none-{optId}))");
            target = new SmtTypedTerm($"(opt-val-{optId} {subject.Term})", innerType!);
        }

        if (recPat.DeclaredSymbol is ILocalSymbol locSym)
        {
            env.Vars[locSym] = target;
        }

        foreach (var sub in recPat.PropertySubpatterns)
        {
            var memberName = sub.Member switch
            {
                IPropertyReferenceOperation pr => pr.Property.Name,
                IFieldReferenceOperation fr => fr.Field.Name,
                _ => null
            };

            if (memberName is null)
            {
                throw new ContractLoweringException($"Unsupported property subpattern member '{sub.Member.Kind}' in specification.");
            }

            var memberTerm = ResolveMemberAccess(target, memberName, emitObligations, inlineDepth);
            var subPat = LowerPatternOp(memberTerm, sub.Pattern, gamma, env, emitObligations, inlineDepth);
            clauses.Add(subPat.Term);
        }

        var combined = clauses.Count switch
        {
            0 => "true",
            1 => clauses[0],
            _ => $"(and {string.Join(" ", clauses)})"
        };
        return new SmtTypedTerm(combined, boolType);
    }

    private SmtTypedTerm LowerPropertyReference(
        IPropertyReferenceOperation propRef,
        string gamma,
        BlockEnv env,
        bool emitObligations,
        int inlineDepth)
    {
        var prop = propRef.Property;
        if (propRef.Instance is null && prop.Name is "Now" or "UtcNow" or "Today" &&
            VerifiableSymbolHelpers.IsNondeterministicMember(prop, out var ambient))
            throw new ContractLoweringException($"Ambient property '{ambient}' is not deterministic.");
        if (CalendarArithmetic.CalendarType(prop.ContainingType) is { } calendar)
        {
            if (propRef.Instance is null) return CalendarConstant(calendar, prop.Name, prop.Type);
            var receiver = LowerExpression(propRef.Instance, gamma, env, emitObligations, inlineDepth);
            return CalendarMember(calendar, receiver.Term, prop.Name, prop.Type);
        }

        if (prop.IsIndexer && propRef.Instance is not null && propRef.Arguments.Length == 1)
        {
            var recv = LowerExpression(propRef.Instance, gamma, env, emitObligations, inlineDepth);
            var arg = LowerExpression(propRef.Arguments[0].Value, gamma, env, emitObligations, inlineDepth);
            if (_types.TryGetDictionaryInfo(recv.Type, out _, out var dictId, out var kType, out var vType))
            {
                var kConv = _translator.ConvertTo(arg, kType!);
                if (emitObligations)
                {
                    AddObligation("CollectionBounds", $"Dictionary key exists for indexer '{propRef.Syntax}'", propRef.Syntax.GetLocation(), gamma,
                        $"(select (map_has_{dictId} {recv.Term}) {kConv.Term})");
                }
                var valTerm = new SmtTypedTerm($"(select (map_data_{dictId} {recv.Term}) {kConv.Term})", vType!);
                EmitOnDemandElementAssumptions($"(select (map_has_{dictId} {recv.Term}) {kConv.Term})", valTerm, propRef.Syntax.GetLocation());
                return valTerm;
            }
            if (_types.TryGetSequenceInfo(recv.Type, out _, out var seqId, out var eType))
            {
                var idxTerm = _translator.IntTermForIndex(arg);
                var boundsCond = $"(and (>= {idxTerm} 0) (< {idxTerm} (seq_count_{seqId} {recv.Term})))";
                if (emitObligations)
                {
                    AddObligation("CollectionBounds", $"Sequence index within bounds for '{propRef.Syntax}'", propRef.Syntax.GetLocation(), gamma, boundsCond);
                }
                var elemVal = new SmtTypedTerm($"(select (seq_elems_{seqId} {recv.Term}) {idxTerm})", eType!);
                EmitOnDemandElementAssumptions(boundsCond, elemVal, propRef.Syntax.GetLocation());
                return elemVal;
            }
        }

        if (propRef.Instance is null)
        {
            var containing = prop.ContainingType?.OriginalDefinition.ToDisplayString() ?? string.Empty;
            if (containing.StartsWith("System.Collections.Immutable.", StringComparison.Ordinal) && prop.Name == "Empty")
            {
                return new SmtTypedTerm(_types.GetEmptyCollectionTerm(prop.Type), prop.Type);
            }
            if (containing == "System.String" && prop.Name == "Empty")
            {
                return new SmtTypedTerm("(str-val \"\")", prop.Type);
            }
            if (containing == "Verifiable.Contracts.Unit" && prop.Name == "Value")
            {
                return new SmtTypedTerm(_types.GetDefaultTerm(prop.Type), prop.Type);
            }
        }
        else
        {
            var recv = LowerExpression(propRef.Instance, gamma, env, emitObligations, inlineDepth);
            return ResolveMemberAccess(recv, prop.Name, emitObligations, inlineDepth);
        }

        if (!emitObligations)
            throw new ContractLoweringException($"Unsupported property reference '{prop.Name}' in specification.");
        return BindSsaValue("prop", new SmtTypedTerm(_types.GetDefaultTerm(prop.Type), prop.Type), isFreshFreeSymbol: true);
    }

    private SmtTypedTerm LowerFieldReference(
        IFieldReferenceOperation fieldRef,
        string gamma,
        BlockEnv env,
        bool emitObligations,
        int inlineDepth)
    {
        var field = fieldRef.Field;
        if (CalendarArithmetic.CalendarType(field.ContainingType) is { } calendar && !field.HasConstantValue)
            return CalendarConstant(calendar, field.Name, field.Type);
        if (fieldRef.Instance is null)
        {
            if (field.ContainingType.SpecialType == SpecialType.System_String && field.Name == "Empty")
                return new SmtTypedTerm("(str-val \"\")", field.Type);
            if (field.HasConstantValue && field.Type.SpecialType == SpecialType.System_String)
                return new SmtTypedTerm(field.ConstantValue is string s
                    ? $"(str-val {ContractExpressionTranslator.EscapeSmtString(s)})" : "str-null", field.Type);
            if (field.ConstantValue is char c)
                return new SmtTypedTerm(((int)c).ToString(CultureInfo.InvariantCulture), field.Type);
            var containing = field.ContainingType?.OriginalDefinition.ToDisplayString() ?? string.Empty;
            if (containing.StartsWith("System.Collections.Immutable.", StringComparison.Ordinal) && field.Name == "Empty")
            {
                return new SmtTypedTerm(_types.GetEmptyCollectionTerm(field.Type), field.Type);
            }
            if (field.ContainingType?.ToDisplayString() == "System.Guid" && field.Name == "Empty")
            {
                return new SmtTypedTerm("0", field.Type);
            }
            if (field.HasConstantValue && field.ConstantValue is not null)
            {
                if (field.Type.TypeKind == TypeKind.Enum)
                {
                    return new SmtTypedTerm(FormatEnumConstant(field.ConstantValue), field.Type);
                }
                if (field.ConstantValue is int iv) return new SmtTypedTerm(_translator.FormatIntLiteral(iv, 32), field.Type);
                if (field.ConstantValue is long lv) return new SmtTypedTerm(_translator.FormatLongLiteral(lv), field.Type);
                if (field.ConstantValue is decimal dv) return new SmtTypedTerm(ContractExpressionTranslator.FormatDecimalLiteral(dv), field.Type);
            }
            if (field.ContainingType?.OriginalDefinition.ToDisplayString() == "Verifiable.Contracts.Unit" && field.Name == "Value")
            {
                return new SmtTypedTerm(_types.GetDefaultTerm(field.Type), field.Type);
            }
            if (!emitObligations)
                throw new ContractLoweringException($"Unsupported static field '{field.Name}' in specification.");
            return BindSsaValue("fld", new SmtTypedTerm(_types.GetDefaultTerm(field.Type), field.Type), isFreshFreeSymbol: true);
        }

        var recv = LowerExpression(fieldRef.Instance, gamma, env, emitObligations, inlineDepth);
        return ResolveMemberAccess(recv, field.Name, emitObligations, inlineDepth);
    }

    private SmtTypedTerm ResolveMemberAccess(SmtTypedTerm receiver, string memberName, bool emitObligations, int inlineDepth)
    {
        if (_translator.TryTranslateMemberOnReceiver(receiver, memberName) is { } direct)
        {
            return direct;
        }

        var prop = receiver.Type.GetMembers(memberName).OfType<IPropertySymbol>().FirstOrDefault();
        if (prop is not null && inlineDepth < 8 &&
            SmtTypeSystem.TryExtractSingleReturnOperation(_compilation, prop, out _, out var bodyOp))
        {
            var propEnv = new BlockEnv { ThisOverride = receiver };
            return LowerExpression(bodyOp!, "true", propEnv, emitObligations: false, inlineDepth + 1);
        }

        throw new ContractLoweringException($"Member '{memberName}' not found or unsupported on '{receiver.Type.Name}'.");
    }

    private SmtTypedTerm LowerWithOperation(IWithOperation withOp, string gamma, BlockEnv env, bool emitObligations, int inlineDepth)
    {
        var baseVal = LowerExpression(withOp.Operand, gamma, env, emitObligations, inlineDepth);
        if (!_types.TryGetComposite(withOp.Type!, out var comp))
        {
            return baseVal;
        }

        var overrides = new Dictionary<string, SmtTypedTerm>(StringComparer.Ordinal);
        if (withOp.Initializer is not null)
        {
            foreach (var init in withOp.Initializer.Initializers)
            {
                if (init is ISimpleAssignmentOperation sa && sa.Target is IMemberReferenceOperation mr)
                {
                    var val = LowerExpression(sa.Value, gamma, env, emitObligations, inlineDepth);
                    overrides[mr.Member.Name] = _translator.ConvertTo(val, mr.Member is IPropertySymbol p ? p.Type : ((IFieldSymbol)mr.Member).Type);
                }
            }
        }

        if (comp!.Fields.Count == 0)
        {
            return new SmtTypedTerm(comp.ConstructorName, withOp.Type!);
        }

        var fieldTerms = new List<string>(comp.Fields.Count);
        foreach (var f in comp.Fields)
        {
            if (overrides.TryGetValue(f.Name, out var ov))
            {
                fieldTerms.Add(ov.Term);
            }
            else
            {
                fieldTerms.Add($"({f.AccessorName} {baseVal.Term})");
            }
        }

        var constructed = new SmtTypedTerm($"({comp.ConstructorName} {string.Join(" ", fieldTerms)})", withOp.Type!);
        return BindSsaValue("with", constructed);
    }

    private SmtTypedTerm LowerObjectCreation(IObjectCreationOperation objCreate, string gamma, BlockEnv env, bool emitObligations, int inlineDepth)
    {
        // Roslyn splits a body initializer into a fresh capture plus member writes.
        // Recover its original tree to validate the lifetime and setter semantics.
        var hasInitializer = objCreate.Syntax is BaseObjectCreationExpressionSyntax { Initializer: { } initSyntax } &&
            initSyntax.IsKind(SyntaxKind.ObjectInitializerExpression);
        if (hasInitializer)
        {
            var original = objCreate.Initializer is not null ? objCreate :
                _compilation.GetSemanticModel(objCreate.Syntax.SyntaxTree).GetOperation(objCreate.Syntax) as IObjectCreationOperation;
            if (original is null || !InitializerProjection.IsSupported(original, allowBoundaryDto: true))
                throw new ContractLoweringException($"Unsupported constructor, setter, or non-write-once projection in '{objCreate.Syntax}'.");
        }
        var targetType = objCreate.Type!;
        if (CalendarArithmetic.CalendarType(targetType) is { } calendar)
            return LowerCalendarConstructor(calendar, objCreate, gamma, env, emitObligations, inlineDepth);
        if (!_types.TryGetComposite(targetType, out var comp))
        {
            return new SmtTypedTerm(_types.GetDefaultTerm(targetType), targetType);
        }

        var argMap = new Dictionary<string, SmtTypedTerm>(hasInitializer ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        foreach (var arg in objCreate.Arguments)
        {
            if (arg.Parameter is not null)
            {
                var v = LowerExpression(arg.Value, gamma, env, emitObligations, inlineDepth);
                argMap[arg.Parameter.Name] = _translator.ConvertTo(v, arg.Parameter.Type);
                if (_closedEnums && objCreate.Constructor is { } ctor &&
                    VerifiableSymbolHelpers.GetScope(ctor) == VerificationScope.Verifiable)
                    CheckEnumArgument(argMap[arg.Parameter.Name], ctor, gamma, objCreate.Syntax.GetLocation());
            }
        }

        if (objCreate.Initializer is not null)
        {
            foreach (var init in objCreate.Initializer.Initializers)
            {
                if (init is ISimpleAssignmentOperation sa && sa.Target is IMemberReferenceOperation mr)
                {
                    var v = LowerExpression(sa.Value, gamma, env, emitObligations, inlineDepth);
                    argMap[mr.Member.Name] = v;
                }
            }
        }

        if (comp!.Fields.Count == 0)
        {
            return new SmtTypedTerm(comp.ConstructorName, targetType);
        }

        var args = comp.Fields.Select(f =>
            argMap.TryGetValue(f.Name, out var v) ? _translator.ConvertTo(v, f.Type).Term : _types.GetDefaultTerm(f.Type));

        var constructed = new SmtTypedTerm($"({comp.ConstructorName} {string.Join(" ", args)})", targetType);
        return BindSsaValue("new_" + SmtTypeSystem.SanitizeName(targetType.Name), constructed);
    }

    private SmtTypedTerm LowerTuple(ITupleOperation tuple, string gamma, BlockEnv env, bool emitObligations, int inlineDepth)
    {
        var tupleType = tuple.Type!;
        if (!_types.TryGetComposite(tupleType, out var comp) || comp!.Fields.Count == 0)
        {
            return new SmtTypedTerm(_types.GetDefaultTerm(tupleType), tupleType);
        }

        var elems = new List<string>(tuple.Elements.Length);
        for (var i = 0; i < tuple.Elements.Length && i < comp.Fields.Count; i++)
        {
            var e = LowerExpression(tuple.Elements[i], gamma, env, emitObligations, inlineDepth);
            elems.Add(_translator.ConvertTo(e, comp.Fields[i].Type).Term);
        }

        return BindSsaValue("tup", new SmtTypedTerm($"({comp.ConstructorName} {string.Join(" ", elems)})", tupleType));
    }

    private SmtTypedTerm LowerCollectionExpression(ICollectionExpressionOperation colExpr, string gamma, BlockEnv env, bool emitObligations, int inlineDepth)
    {
        var t = colExpr.Type!;
        if (_types.TryGetSequenceInfo(t, out var seqSort, out var seqId, out var elemType))
        {
            var cur = new SmtTypedTerm(_types.GetEmptyCollectionTerm(t), t);
            var i = 0;
            foreach (var elemOp in colExpr.Elements)
            {
                var ev = _translator.ConvertTo(LowerExpression(elemOp, gamma, env, emitObligations, inlineDepth), elemType!);
                cur = new SmtTypedTerm(
                    $"(mk-{seqSort} (store (seq_elems_{seqId} {cur.Term}) {i} {ev.Term}) {i + 1})",
                    t);
                i++;
            }
            return BindSsaValue("seq", cur);
        }

        return new SmtTypedTerm(_types.GetDefaultTerm(t), t);
    }

    private SmtTypedTerm LowerInvocation(
        IInvocationOperation inv,
        string gamma,
        BlockEnv env,
        bool emitObligations,
        int inlineDepth)
    {
        var method = inv.TargetMethod;
        var containing = method.ContainingType;
        var loc = inv.Syntax.GetLocation();

        // 0. Spec.* intrinsics
        if (containing?.ToDisplayString() == VerifiableSymbolHelpers.SpecTypeName)
        {
            if (method.Name == "Result")
            {
                if (env.SpecResult is null)
                {
                    throw new ContractLoweringException("Spec.Result<TResult>() can only be used inside Spec.Ensures(...).");
                }
                if (method.TypeArguments.Length == 1 &&
                    !SymbolEqualityComparer.Default.Equals(method.TypeArguments[0], env.SpecResult.Type))
                {
                    throw new ContractLoweringException($"Spec.Result<{method.TypeArguments[0].Name}>() does not match method return type '{env.SpecResult.Type.Name}'.");
                }
                return env.SpecResult;
            }

            // Requires / Ensures / LoopInvariant / Decreases are compile-time annotations processed outside statement simulation
            return new SmtTypedTerm("0", _compilation.GetSpecialType(SpecialType.System_Int32));
        }

        if (VerifiableSymbolHelpers.IsLinqQueryOperator(method) || !emitObligations && VerifiableSymbolHelpers.IsLinqProvider(method))
            return LowerImmutableQuery(inv, gamma, env, emitObligations, inlineDepth);

        if (RegexModel.IsMatchMethod(method))
        {
            var (input, regex) = RegexModel.Describe(inv, _compilation);
            var value = LowerExpression(input, gamma, env, emitObligations, inlineDepth);
            if (emitObligations)
                AddObligation("Precondition", "Regex.IsMatch input must not be null", loc, gamma, $"(not (= {value.Term} str-null))");
            return new SmtTypedTerm($"(str.in_re (str-text {value.Term}) {regex})", method.ReturnType);
        }

        if (CalendarArithmetic.CalendarType(containing) is not null ||
            containing?.SpecialType == SpecialType.System_Decimal && method.Name == "Round")
            return LowerCalendarInvocation(inv, gamma, env, emitObligations, inlineDepth);

        var recvTerm = inv.Instance is not null ? LowerExpression(inv.Instance, gamma, env, emitObligations, inlineDepth) : null;
        if (StringIntrinsics.Handles(method))
            return LowerStringIntrinsic(inv, recvTerm, gamma, env, emitObligations, inlineDepth);

        // The CFG for `nullable ?? fallback` extracts the present payload with this call.
        if (recvTerm is not null && containing?.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
            method.Name == "GetValueOrDefault" && inv.Arguments.Length == 0 &&
            _types.TryGetNullableInfo(recvTerm.Type, out _, out var optId, out var payloadType))
        {
            return new SmtTypedTerm($"(ite {NullTest(recvTerm)} {_types.GetDefaultTerm(payloadType!)} (opt-val-{optId} {recvTerm.Term}))", payloadType!);
        }
        var evaluated = new List<(int Ordinal, SmtTypedTerm Value)>(inv.Arguments.Length);
        foreach (var arg in inv.Arguments)
        {
            var v = LowerExpression(arg.Value, gamma, env, emitObligations, inlineDepth);
            evaluated.Add((arg.Parameter?.Ordinal ?? evaluated.Count, _translator.ConvertTo(v, arg.Parameter?.Type ?? v.Type)));
        }
        // Roslyn supplies implicit default arguments as bound operations. Evaluate in
        // C# order, but interpret the resulting values in formal parameter order.
        var argValues = evaluated.OrderBy(a => a.Ordinal).Select(a => a.Value).ToList();
        if (_closedEnums && VerifiableSymbolHelpers.GetScope(method) == VerificationScope.Verifiable)
        {
            if (recvTerm is not null) CheckEnumArgument(recvTerm, method, gamma, loc);
            foreach (var arg in argValues) CheckEnumArgument(arg, method, gamma, loc);
        }

        // 1. Constrained [TypeAlias<T>] / [Refined<T>]: From(value) and IsValid(value)
        if (containing is not null && SmtTypeSystem.TryGetConstrainedUnderlying(containing, out var underlying))
        {
            if (method.Name == "From" && argValues.Count == 1 && _types.TryGetComposite(containing, out var refComp))
            {
                var arg = _translator.ConvertTo(argValues[0], underlying!);
                if (!_expandingCalls.Add(method.OriginalDefinition))
                    throw new ContractLoweringException($"Recursive specification expansion of '{method.Name}' is not supported.");
                try
                {
                    CheckMethodRequires(method, recvTerm, new[] { arg }, gamma, loc);
                }
                finally
                {
                    _expandingCalls.Remove(method.OriginalDefinition);
                }
                return BindSsaValue("ref_" + SmtTypeSystem.SanitizeName(containing.Name),
                    new SmtTypedTerm($"({refComp!.ConstructorName} {arg.Term})", containing));
            }
            if (method.Name == "IsValid" && argValues.Count == 1 && inlineDepth < 8 &&
                SmtTypeSystem.TryExtractSingleReturnOperation(_compilation, method, out var isValidParam, out var isValidBody))
            {
                var valEnv = new BlockEnv();
                if (isValidParam is not null) valEnv.Vars[isValidParam] = _translator.ConvertTo(argValues[0], underlying!);
                return LowerExpression(isValidBody!, gamma, valEnv, emitObligations: false, inlineDepth + 1);
            }
        }

        // 2. Result<T, TError>.Ok(v) / Fail(e)
        if (containing?.OriginalDefinition.ToDisplayString() == "Verifiable.Contracts.Result<T, TError>" &&
            _types.TryGetComposite(containing, out var resComp))
        {
            var tArg = containing.TypeArguments[0];
            var eArg = containing.TypeArguments[1];
            if (method.Name == "Ok" && argValues.Count == 1)
            {
                var vTerm = _translator.ConvertTo(argValues[0], tArg);
                return BindSsaValue("resOk", new SmtTypedTerm($"({resComp!.ConstructorName} true {vTerm.Term} {_types.GetDefaultTerm(eArg)})", containing));
            }
            if (method.Name == "Fail" && argValues.Count == 1)
            {
                var eTerm = _translator.ConvertTo(argValues[0], eArg);
                return BindSsaValue("resFail", new SmtTypedTerm($"({resComp!.ConstructorName} false {_types.GetDefaultTerm(tArg)} {eTerm.Term})", containing));
            }
        }

        // 3. ImmutableDictionary<K, V> methods
        if (recvTerm is not null && _types.TryGetDictionaryInfo(recvTerm.Type, out var mapSort, out var dictId, out var kType, out var vType))
        {
            if (method.Name == "ContainsKey" && argValues.Count == 1)
            {
                var k = _translator.ConvertTo(argValues[0], kType!);
                return new SmtTypedTerm($"(select (map_has_{dictId} {recvTerm.Term}) {k.Term})", _compilation.GetSpecialType(SpecialType.System_Boolean));
            }
            if (method.Name == "GetValueOrDefault" && argValues.Count >= 1)
            {
                var k = _translator.ConvertTo(argValues[0], kType!);
                var def = argValues.Count == 2 ? _translator.ConvertTo(argValues[1], vType!).Term : _types.GetDefaultTerm(vType!);
                EmitOnDemandElementAssumptions($"(select (map_has_{dictId} {recvTerm.Term}) {k.Term})",
                    new SmtTypedTerm($"(select (map_data_{dictId} {recvTerm.Term}) {k.Term})", vType!), loc);
                return new SmtTypedTerm($"(ite (select (map_has_{dictId} {recvTerm.Term}) {k.Term}) (select (map_data_{dictId} {recvTerm.Term}) {k.Term}) {def})", vType!);
            }
            if (method.Name == "Add" && argValues.Count == 2)
            {
                var k = _translator.ConvertTo(argValues[0], kType!);
                var v = _translator.ConvertTo(argValues[1], vType!);
                if (emitObligations)
                {
                    AddObligation("CollectionBounds", "Key not already present before ImmutableDictionary.Add", loc, gamma,
                        $"(not (select (map_has_{dictId} {recvTerm.Term}) {k.Term}))");
                }
                var updated = new SmtTypedTerm(
                    $"(mk-{mapSort} (store (map_data_{dictId} {recvTerm.Term}) {k.Term} {v.Term}) (store (map_has_{dictId} {recvTerm.Term}) {k.Term} true) (ite (select (map_has_{dictId} {recvTerm.Term}) {k.Term}) (map_count_{dictId} {recvTerm.Term}) (+ (map_count_{dictId} {recvTerm.Term}) 1)))",
                    recvTerm.Type);
                return BindSsaValue("dictAdd", updated);
            }
            if (method.Name == "SetItem" && argValues.Count == 2)
            {
                var k = _translator.ConvertTo(argValues[0], kType!);
                var v = _translator.ConvertTo(argValues[1], vType!);
                var updated = new SmtTypedTerm(
                    $"(mk-{mapSort} (store (map_data_{dictId} {recvTerm.Term}) {k.Term} {v.Term}) (store (map_has_{dictId} {recvTerm.Term}) {k.Term} true) (ite (select (map_has_{dictId} {recvTerm.Term}) {k.Term}) (map_count_{dictId} {recvTerm.Term}) (+ (map_count_{dictId} {recvTerm.Term}) 1)))",
                    recvTerm.Type);
                return BindSsaValue("dictSet", updated);
            }
            if (method.Name == "Remove" && argValues.Count == 1)
            {
                var k = _translator.ConvertTo(argValues[0], kType!);
                var updated = new SmtTypedTerm(
                    $"(mk-{mapSort} (map_data_{dictId} {recvTerm.Term}) (store (map_has_{dictId} {recvTerm.Term}) {k.Term} false) (ite (select (map_has_{dictId} {recvTerm.Term}) {k.Term}) (- (map_count_{dictId} {recvTerm.Term}) 1) (map_count_{dictId} {recvTerm.Term})))",
                    recvTerm.Type);
                return BindSsaValue("dictRem", updated);
            }
        }

        // 4. ImmutableList<T> / ImmutableArray<T> methods
        if (recvTerm is not null && _types.TryGetSequenceInfo(recvTerm.Type, out var seqSort, out var seqId, out var seqElemType))
        {
            if (method.Name == "Add" && argValues.Count == 1)
            {
                var v = _translator.ConvertTo(argValues[0], seqElemType!);
                var updated = new SmtTypedTerm(
                    $"(mk-{seqSort} (store (seq_elems_{seqId} {recvTerm.Term}) (seq_count_{seqId} {recvTerm.Term}) {v.Term}) (+ (seq_count_{seqId} {recvTerm.Term}) 1))",
                    recvTerm.Type);
                return BindSsaValue("seqAdd", updated);
            }
            if (method.Name == "SetItem" && argValues.Count == 2)
            {
                var idxTerm = _translator.IntTermForIndex(argValues[0]);
                var v = _translator.ConvertTo(argValues[1], seqElemType!);
                if (emitObligations)
                {
                    AddObligation("CollectionBounds", "Sequence index within bounds for SetItem", loc, gamma,
                        $"(and (>= {idxTerm} 0) (< {idxTerm} (seq_count_{seqId} {recvTerm.Term})))");
                }
                var updated = new SmtTypedTerm(
                    $"(mk-{seqSort} (store (seq_elems_{seqId} {recvTerm.Term}) {idxTerm} {v.Term}) (seq_count_{seqId} {recvTerm.Term}))",
                    recvTerm.Type);
                return BindSsaValue("seqSet", updated);
            }
        }

        // 5. ImmutableHashSet<T> methods
        if (recvTerm is not null && _types.TryGetSetInfo(recvTerm.Type, out var setSort, out var setId, out var setElemType))
        {
            if (method.Name == "Contains" && argValues.Count == 1)
            {
                var v = _translator.ConvertTo(argValues[0], setElemType!);
                return new SmtTypedTerm($"(select (set_has_{setId} {recvTerm.Term}) {v.Term})", _compilation.GetSpecialType(SpecialType.System_Boolean));
            }
            if (method.Name == "Add" && argValues.Count == 1)
            {
                var v = _translator.ConvertTo(argValues[0], setElemType!);
                var updated = new SmtTypedTerm(
                    $"(mk-{setSort} (store (set_has_{setId} {recvTerm.Term}) {v.Term} true) (ite (select (set_has_{setId} {recvTerm.Term}) {v.Term}) (set_count_{setId} {recvTerm.Term}) (+ (set_count_{setId} {recvTerm.Term}) 1)))",
                    recvTerm.Type);
                return BindSsaValue("setAdd", updated);
            }
            if (method.Name == "Remove" && argValues.Count == 1)
            {
                var v = _translator.ConvertTo(argValues[0], setElemType!);
                var updated = new SmtTypedTerm(
                    $"(mk-{setSort} (store (set_has_{setId} {recvTerm.Term}) {v.Term} false) (ite (select (set_has_{setId} {recvTerm.Term}) {v.Term}) (- (set_count_{setId} {recvTerm.Term}) 1) (set_count_{setId} {recvTerm.Term})))",
                    recvTerm.Type);
                return BindSsaValue("setRem", updated);
            }
        }

        // 6. Immutable collection static Create()
        if (containing?.OriginalDefinition.ToDisplayString() is
            "System.Collections.Immutable.ImmutableList" or
            "System.Collections.Immutable.ImmutableArray" or
            "System.Collections.Immutable.ImmutableDictionary" or
            "System.Collections.Immutable.ImmutableHashSet" &&
            method.Name == "Create" && argValues.Count == 0)
        {
            return new SmtTypedTerm(_types.GetEmptyCollectionTerm(method.ReturnType), method.ReturnType);
        }

        // 7. Math / decimal / string helpers
        if (containing?.SpecialType is SpecialType.System_Decimal || containing?.ToDisplayString() == "System.Math")
        {
            if (method.Name == "Abs" && argValues.Count == 1)
            {
                var a = _translator.UnwrapRefinedIfApplicable(argValues[0]);
                var sort = _types.GetSort(a.Type);
                var zero = sort == "Real" ? "0.0" : _translator.FormatIntLiteral(0, 32);
                var lt = ContractExpressionTranslator.IsBitVecSort(sort) ? "bvslt" : "<";
                var neg = ContractExpressionTranslator.IsBitVecSort(sort) ? "bvneg" : "-";
                return new SmtTypedTerm($"(ite ({lt} {a.Term} {zero}) ({neg} {a.Term}) {a.Term})", method.ReturnType);
            }
            if (method.Name == "Max" && argValues.Count == 2)
            {
                var (a, b, u) = _translator.UnifyNumericPair(argValues[0], argValues[1]);
                var ge = ContractExpressionTranslator.IsBitVecSort(_types.GetSort(u)) ? "bvsge" : ">=";
                return new SmtTypedTerm($"(ite ({ge} {a} {b}) {a} {b})", method.ReturnType);
            }
            if (method.Name == "Min" && argValues.Count == 2)
            {
                var (a, b, u) = _translator.UnifyNumericPair(argValues[0], argValues[1]);
                var le = ContractExpressionTranslator.IsBitVecSort(_types.GetSort(u)) ? "bvsle" : "<=";
                return new SmtTypedTerm($"(ite ({le} {a} {b}) {a} {b})", method.ReturnType);
            }
        }

        if (method.Name == "Equals" && recvTerm is not null && argValues.Count == 1 &&
            (method.IsImplicitlyDeclared || method.DeclaringSyntaxReferences.Length == 0))
        {
            if (ContainsUnmodeledReference(recvTerm.Type) || ContainsUnmodeledReference(method.ContainingType))
                throw new ContractLoweringException("Reference identity is not modeled for this type or its stored fields; compare individual properties instead.");
            var (l, r, _) = _translator.UnifyForComparison(recvTerm, argValues[0]);
            return new SmtTypedTerm($"(= {l} {r})", _compilation.GetSpecialType(SpecialType.System_Boolean));
        }

        if (!_expandingCalls.Add(method.OriginalDefinition))
        {
            throw new ContractLoweringException($"Recursive specification expansion of '{method.Name}' is not supported.");
        }
        try
        {
            return LowerHelperCall(method, recvTerm, argValues, gamma, loc, emitObligations, inlineDepth);
        }
        finally
        {
            _expandingCalls.Remove(method.OriginalDefinition);
        }
    }

    private SmtTypedTerm LowerHelperCall(IMethodSymbol method, SmtTypedTerm? receiver, IReadOnlyList<SmtTypedTerm> args,
        string gamma, Location loc, bool implementationCall, int inlineDepth)
    {
        var specs = ExtractMethodSpecs(_compilation, method, default);
        _errors.AddRange(specs.Errors);
        var calleeEnv = new BlockEnv { ThisOverride = receiver };
        for (var i = 0; i < method.Parameters.Length && i < args.Count; i++)
            calleeEnv.Vars[method.Parameters[i]] = args[i];

        CheckMethodRequires(method, specs, calleeEnv, gamma, loc);

        // Neither a callee summary nor nested safety checks in its definition
        // apply when the caller has already violated its entry assumptions.
        var summaryGuard = gamma;
        if (_closedEnums && VerifiableSymbolHelpers.GetScope(method) == VerificationScope.Verifiable)
        {
            foreach (var input in receiver is null ? args : args.Concat(new[] { receiver }))
                foreach (var domain in EntryEnumDomains(_types, input))
                    summaryGuard = CombineAnd(summaryGuard, domain);
        }

        var definitional = VerifiableSymbolHelpers.GetScope(method) == VerificationScope.Verifiable &&
            !VerifiableSymbolHelpers.HasAttribute(method, VerifiableSymbolHelpers.GhostAttr);
        SmtTypedTerm? definition = null;
        if ((!implementationCall || definitional) && !method.ReturnsVoid &&
            !VerifiableSymbolHelpers.HasAttribute(method, VerifiableSymbolHelpers.PureAxiomAttr) &&
            HasCheckedArithmetic(method) == _types.UseBitVec &&
            !method.IsGenericMethod && !method.ContainingType.IsGenericType &&
            (!method.IsVirtual && !method.IsOverride || method.IsSealed || method.ContainingType.IsSealed) &&
            !method.ReturnsByRef && !method.ReturnsByRefReadonly && method.Parameters.All(p => p.RefKind == RefKind.None) &&
            inlineDepth < 8 && SmtTypeSystem.TryExtractSingleReturnOperation(_compilation, method, out _, out var body, allowLeadingContracts: true) &&
            CanUseHelperBody(body!))
        {
            try
            {
                definition = _translator.ConvertTo(LowerExpression(body!, summaryGuard, calleeEnv, emitObligations: false, inlineDepth + 1), method.ReturnType);
            }
            catch (ContractLoweringException) when (implementationCall)
            {
                // Non-definitional calls retain their usual modular interpretation.
            }
        }

        if (!implementationCall)
            return definition ?? throw new ContractLoweringException($"Unsupported method call '{method.ContainingType?.Name}.{method.Name}' inside specification.");

        if (method.ReturnsVoid)
        {
            return new SmtTypedTerm("0", _compilation.GetSpecialType(SpecialType.System_Int32));
        }

        var retTerm = BindSsaValue("call_" + SmtTypeSystem.SanitizeName(method.Name),
            new SmtTypedTerm(_types.GetDefaultTerm(method.ReturnType), method.ReturnType),
            isFreshFreeSymbol: true);

        if (definition is not null)
            AddAssertion($"(=> {gamma} (= {retTerm.Term} {definition.Term}))", "HelperBody", method.Name, loc);
        EmitTypeAssumptions(retTerm, loc);

        if (specs.Ensures.Count > 0)
        {
            calleeEnv.SpecResult = retTerm;
            foreach (var ens in specs.Ensures)
            {
                if (ens.LambdaParam is not null)
                {
                    calleeEnv.Vars[ens.LambdaParam] = retTerm;
                }

                try
                {
                    var ensTerm = LowerExpression(ens.BodyExpr, gamma, calleeEnv, emitObligations: false);
                    var ensLoc = ens.Location != Location.None ? ens.Location : loc;
                    AddAssertion(
                        $"(=> {summaryGuard} {ensTerm.Term})",
                        "CalleeEnsures",
                        $"{method.ContainingType?.Name}.{method.Name}: {ens.DisplayText}",
                        ensLoc,
                        ens.Because);
                }
                catch (ContractLoweringException ex)
                {
                    _errors.Add((ens.DisplayText, loc, ex.Message));
                }
            }
        }

        return retTerm;
    }

    private bool CanUseHelperBody(IOperation body) =>
        !body.DescendantsAndSelf().Any(op => op is IAssignmentOperation &&
            op.Parent is not IObjectOrCollectionInitializerOperation { Parent: IObjectCreationOperation } ||
            op is IIncrementOrDecrementOperation
            or ILocalReferenceOperation or IAnonymousFunctionOperation ||
            op is IConversionOperation { OperatorMethod: not null } ||
            op is IBinaryOperation { OperatorMethod: { } binary } && !IsBuiltInValueOperator(binary) ||
            op is IUnaryOperation { OperatorMethod: { } unary } && !IsBuiltInValueOperator(unary) ||
            op is IObjectCreationOperation obj && !(obj.Initializer is null
                ? IsPositionalProjection(obj)
                : InitializerProjection.IsSupported(obj, allowBoundaryDto: false)));

    private static bool IsBuiltInValueOperator(IMethodSymbol method) =>
        CalendarArithmetic.CalendarType(method.ContainingType) != null ||
        method.ContainingType.SpecialType is SpecialType.System_Decimal or SpecialType.System_String ||
        method.IsImplicitlyDeclared && method.ContainingType.IsRecord;

    private static bool ContainsUnmodeledReference(ITypeSymbol? type) =>
        ContainsUnmodeledReference(type, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default));

    private static bool ContainsUnmodeledReference(ITypeSymbol? type, HashSet<ITypeSymbol> path)
    {
        if (type is null || type.SpecialType == SpecialType.System_String || type.TypeKind == TypeKind.Enum)
            return false;
        if (type is not INamedTypeSymbol named)
            return true;
        // A record's synthesized Equals compares instance fields (including auto-
        // property backing fields) with EqualityComparer<T>.Default. Wrapping a
        // DTO in a struct or another record does not make its identity structural.
        if (!path.Add(named.OriginalDefinition))
            return true;
        try
        {
            if (named.OriginalDefinition.ToDisplayString() is "System.Collections.Immutable.ImmutableList<T>" or
                "System.Collections.Immutable.ImmutableArray<T>" or "System.Collections.Immutable.ImmutableDictionary<TKey, TValue>" or
                "System.Collections.Immutable.ImmutableHashSet<T>" || named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
                return named.TypeArguments.Any(t => ContainsUnmodeledReference(t, path));
            if (!named.IsRecord && named.IsReferenceType)
                return true;
            if (named.SpecialType != SpecialType.None)
                return false;
            if (named.IsTupleType)
                return named.TupleElements.Any(e => ContainsUnmodeledReference(e.Type, path));
            return named.BaseType is { IsRecord: true } parent && ContainsUnmodeledReference(parent, path) ||
                named.GetMembers().OfType<IFieldSymbol>().Any(f => !f.IsStatic && ContainsUnmodeledReference(f.Type, path));
        }
        finally
        {
            path.Remove(named.OriginalDefinition);
        }
    }

    private bool IsPositionalProjection(IObjectCreationOperation obj)
    {
        // LowerObjectCreation maps arguments to stored fields by name. A user
        // constructor or a redeclared positional property can transform the value.
        if (obj.Initializer is not null || obj.Constructor is not { } ctor ||
            ctor.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not RecordDeclarationSyntax ||
            ctor.ContainingType.BaseType?.IsRecord == true ||
            !_types.TryGetComposite(ctor.ContainingType, out var composite))
            return false;
        return composite!.Fields.Count == ctor.Parameters.Length && ctor.Parameters.All(p =>
            ctor.ContainingType.GetMembers(p.Name).OfType<IPropertySymbol>().Any(prop =>
                prop.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is ParameterSyntax));
    }

    private void CheckMethodRequires(
        IMethodSymbol callee,
        SmtTypedTerm? recvTerm,
        IReadOnlyList<SmtTypedTerm> args,
        string gamma,
        Location callLoc)
    {
        var calleeSpecs = ExtractMethodSpecs(_compilation, callee, default);
        _errors.AddRange(calleeSpecs.Errors);

        if (calleeSpecs.Requires.Count == 0) return;

        var calleeEnv = new BlockEnv { ThisOverride = recvTerm };
        for (var i = 0; i < callee.Parameters.Length && i < args.Count; i++)
        {
            calleeEnv.Vars[callee.Parameters[i]] = args[i];
        }

        CheckMethodRequires(callee, calleeSpecs, calleeEnv, gamma, callLoc);
    }

    private void CheckMethodRequires(IMethodSymbol callee, MethodSpecs calleeSpecs, BlockEnv calleeEnv, string gamma, Location callLoc)
    {
        foreach (var req in calleeSpecs.Requires)
        {
            try
            {
                var reqTerm = LowerExpression(req.Condition, gamma, calleeEnv, emitObligations: false);
                AddObligation("Precondition", $"{req.DisplayText} on {callee.ContainingType?.Name}.{callee.Name}", callLoc, gamma, reqTerm.Term, req.Because);
            }
            catch (ContractLoweringException ex)
            {
                _errors.Add((req.DisplayText, callLoc, ex.Message));
            }
        }
    }

    private static string FormatEnumConstant(object constant)
    {
        var text = Convert.ToString(constant, CultureInfo.InvariantCulture)!;
        return text.StartsWith("-", StringComparison.Ordinal) ? $"(- {text.Substring(1)})" : text;
    }

    internal static bool IsCheckedEnumCast(IConversionOperation cast, Compilation compilation)
    {
        if (cast.Type?.TypeKind != TypeKind.Enum && cast.Operand.Type?.TypeKind != TypeKind.Enum)
            return false;
        // Roslyn does not consistently set IsChecked for explicit enum casts.
        // The innermost checked/unchecked context takes precedence over /checked.
        foreach (var node in cast.Syntax.AncestorsAndSelf())
        {
            if (node is CheckedExpressionSyntax or CheckedStatementSyntax)
                return node.IsKind(SyntaxKind.CheckedExpression) || node.IsKind(SyntaxKind.CheckedStatement);
        }
        return cast.IsChecked || compilation.Options.CheckOverflow;
    }

    private void AssumeEntryEnumDomains(SmtTypedTerm value, Location loc)
    {
        if (!_closedEnums) return;
        foreach (var domain in EntryEnumDomains(_types, value))
            AddAssertion(domain, "EnumDomain", $"closed enum domain in {value.Type.Name}", loc);
    }

    private void CheckEnumArgument(SmtTypedTerm value, IMethodSymbol callee, string gamma, Location loc)
    {
        foreach (var domain in EntryEnumDomains(_types, value))
            AddObligation("Precondition", $"Closed enum input to {callee.ContainingType.Name}.{callee.Name}", loc, gamma, domain);
    }

    internal static bool HasEnumEntryDomains(IMethodSymbol callee, SmtTypeSystem types) =>
        (!callee.IsStatic && callee.MethodKind != MethodKind.Constructor &&
         EntryEnumDomains(types, new SmtTypedTerm("this", callee.ContainingType)).Any()) ||
        callee.Parameters.Any(p => EntryEnumDomains(types, new SmtTypedTerm(p.Name, p.Type)).Any());

    private static IEnumerable<string> EntryEnumDomains(SmtTypeSystem types, SmtTypedTerm value, HashSet<ITypeSymbol>? ancestors = null)
    {
        if (value.Type.TypeKind == TypeKind.Enum)
        {
            if (value.Type.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "System.FlagsAttribute"))
                yield break;

            var members = value.Type.GetMembers().OfType<IFieldSymbol>()
                .Where(f => f.HasConstantValue && f.ConstantValue is not null)
                .Select(f => FormatEnumConstant(f.ConstantValue!)).Distinct()
                .Select(n => $"(= {value.Term} {n})").ToArray();
            // An empty enum must not make every proof vacuously true.
            if (members.Length > 0)
                yield return members.Length == 1 ? members[0] : $"(or {string.Join(" ", members)})";
        }
        else if (types.TryGetComposite(value.Type, out var composite))
        {
            ancestors ??= new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
            if (!ancestors.Add(value.Type)) yield break;
            var fields = value.Type.GetMembers().OfType<IFieldSymbol>().Where(field => !field.IsStatic).ToArray();
            foreach (var f in composite!.Fields)
            {
                // The general composite model also has synthetic selectors and
                // opaque/computed properties. Only assume facts about storage.
                if (!fields.Any(field => field.Name == f.Name || field.AssociatedSymbol?.Name == f.Name))
                    continue;
                foreach (var domain in EntryEnumDomains(types, new SmtTypedTerm($"({f.AccessorName} {value.Term})", f.Type), ancestors))
                    yield return domain;
            }
            ancestors.Remove(value.Type);
        }
    }

    private void EmitTypeAssumptions(SmtTypedTerm value, Location loc)
    {
        if (CalendarArithmetic.CalendarType(value.Type) is { } calendar)
        {
            AddAssertion(CalendarArithmetic.InRange(calendar, value.Term), "CalendarRange", $"{calendar} range", loc);
            return;
        }
        if (_types.TryGetDictionaryInfo(value.Type, out _, out var dictId, out var kType, out _))
        {
            var kSort = _types.GetSort(kType!);
            AddAssertion($"(>= (map_count_{dictId} {value.Term}) 0)", "CollectionAxiom", $"{value.Type.Name}.Count >= 0", loc);
            AddAssertion($"(= (= (map_count_{dictId} {value.Term}) 0) (= (map_has_{dictId} {value.Term}) ((as const (Array {kSort} Bool)) false)))", "CollectionAxiom", $"{value.Type.Name} empty consistency", loc);
        }
        else if (_types.TryGetSequenceInfo(value.Type, out _, out var seqId, out _))
        {
            AddAssertion($"(>= (seq_count_{seqId} {value.Term}) 0)", "CollectionAxiom", $"{value.Type.Name}.Count >= 0", loc);
        }
        else if (_types.TryGetSetInfo(value.Type, out _, out var setId, out var setElem))
        {
            var eSort = _types.GetSort(setElem!);
            AddAssertion($"(>= (set_count_{setId} {value.Term}) 0)", "CollectionAxiom", $"{value.Type.Name}.Count >= 0", loc);
            AddAssertion($"(= (= (set_count_{setId} {value.Term}) 0) (= (set_has_{setId} {value.Term}) ((as const (Array {eSort} Bool)) false)))", "CollectionAxiom", $"{value.Type.Name} empty consistency", loc);
        }

        if (value.Type is INamedTypeSymbol resNamed &&
            resNamed.OriginalDefinition.ToDisplayString() == "Verifiable.Contracts.Result<T, TError>" &&
            _types.TryGetComposite(resNamed, out var resComp))
        {
            var isSuccess = $"({resComp!.Fields.First(f => f.Name == "IsSuccess").AccessorName} {value.Term})";
            var valField = resComp.Fields.First(f => f.Name == "Value");
            EmitOnDemandElementAssumptions(isSuccess, new SmtTypedTerm($"({valField.AccessorName} {value.Term})", valField.Type), loc);
            return;
        }

        if (SmtTypeSystem.TryGetIsValidMethod(value.Type, out var isValidMethod) &&
            SmtTypeSystem.TryExtractSingleReturnOperation(_compilation, isValidMethod!, out var paramSym, out var isValidBody))
        {
            try
            {
                SmtTypeSystem.TryGetConstrainedUnderlying(value.Type, out _, out var typeDesc);
                var unwrapped = _translator.UnwrapRefinedIfApplicable(value);
                var valEnv = new BlockEnv();
                if (paramSym is not null) valEnv.Vars[paramSym] = unwrapped;
                var invTerm = LowerExpression(isValidBody!, "true", valEnv, emitObligations: false);
                var declLoc = isValidMethod!.Locations.FirstOrDefault() ?? loc;
                AddAssertion(invTerm.Term, "TypeAlias", $"{value.Type.Name}.IsValid", declLoc, typeDesc);
            }
            catch (ContractLoweringException ex)
            {
                _errors.Add(($"{value.Type.Name}.IsValid", loc, ex.Message));
            }
            return;
        }

        foreach (var invMember in SmtTypeSystem.GetInvariantMembers(value.Type))
        {
            if (!SmtTypeSystem.TryExtractSingleReturnOperation(_compilation, invMember, out _, out var invBody))
            {
                _errors.Add(($"{value.Type.Name}.{invMember.Name}", loc,
                    $"[Invariant] member '{invMember.Name}' must be a single-expression method or property."));
                continue;
            }

            try
            {
                var invDesc = SmtTypeSystem.GetInvariantDescription(invMember);
                var invEnv = new BlockEnv { ThisOverride = value };
                var invTerm = LowerExpression(invBody!, "true", invEnv, emitObligations: false);
                var invLoc = invMember.Locations.FirstOrDefault() ?? loc;
                AddAssertion(invTerm.Term, "TypeInvariant", $"[Invariant] {value.Type.Name}.{invMember.Name}", invLoc, invDesc);
            }
            catch (ContractLoweringException ex)
            {
                _errors.Add(($"{value.Type.Name}.{invMember.Name}", loc, ex.Message));
            }
        }

        if (_types.TryGetComposite(value.Type, out var comp) &&
            !SmtTypeSystem.TryGetConstrainedUnderlying(value.Type, out _))
        {
            foreach (var f in comp!.Fields)
            {
                EmitTypeAssumptions(new SmtTypedTerm($"({f.AccessorName} {value.Term})", f.Type), loc);
            }
        }
    }

    private void EmitOnDemandElementAssumptions(string guardCond, SmtTypedTerm element, Location loc)
    {
        var key = guardCond + "|" + element.Term;
        if (!_emittedOnDemandKeys.Add(key)) return;

        if (SmtTypeSystem.TryGetIsValidMethod(element.Type, out var isValidMethod) &&
            SmtTypeSystem.TryExtractSingleReturnOperation(_compilation, isValidMethod!, out var paramSym, out var isValidBody))
        {
            try
            {
                SmtTypeSystem.TryGetConstrainedUnderlying(element.Type, out _, out var typeDesc);
                var unwrapped = _translator.UnwrapRefinedIfApplicable(element);
                var valEnv = new BlockEnv();
                if (paramSym is not null) valEnv.Vars[paramSym] = unwrapped;
                var invTerm = LowerExpression(isValidBody!, "true", valEnv, emitObligations: false);
                var declLoc = isValidMethod!.Locations.FirstOrDefault() ?? loc;
                AddAssertion($"(=> {guardCond} {invTerm.Term})", "TypeAlias", $"{element.Type.Name}.IsValid", declLoc, typeDesc);
            }
            catch (ContractLoweringException ex)
            {
                _errors.Add(($"{element.Type.Name}.IsValid", loc, ex.Message));
            }
            return;
        }

        foreach (var invMember in SmtTypeSystem.GetInvariantMembers(element.Type))
        {
            if (!SmtTypeSystem.TryExtractSingleReturnOperation(_compilation, invMember, out _, out var invBody))
            {
                _errors.Add(($"{element.Type.Name}.{invMember.Name}", loc,
                    $"[Invariant] member '{invMember.Name}' must be a single-expression method or property."));
                continue;
            }

            try
            {
                var invDesc = SmtTypeSystem.GetInvariantDescription(invMember);
                var invEnv = new BlockEnv { ThisOverride = element };
                var invTerm = LowerExpression(invBody!, "true", invEnv, emitObligations: false);
                var invLoc = invMember.Locations.FirstOrDefault() ?? loc;
                AddAssertion($"(=> {guardCond} {invTerm.Term})", "TypeInvariant", $"[Invariant] {element.Type.Name}.{invMember.Name}", invLoc, invDesc);
            }
            catch (ContractLoweringException ex)
            {
                _errors.Add(($"{element.Type.Name}.{invMember.Name}", loc, ex.Message));
            }
        }

        if (_types.TryGetComposite(element.Type, out var comp) &&
            !SmtTypeSystem.TryGetConstrainedUnderlying(element.Type, out _))
        {
            foreach (var f in comp!.Fields)
            {
                EmitOnDemandElementAssumptions(guardCond, new SmtTypedTerm($"({f.AccessorName} {element.Term})", f.Type), loc);
            }
        }
    }

    private (string Gamma, BlockEnv Env) MergeIncomingEdges(List<IncomingEdge> edges)
    {
        if (edges.Count == 1)
        {
            return (edges[0].ReachCond, edges[0].Env.Clone());
        }

        var gamma = edges.Any(e => e.ReachCond == "true")
            ? "true"
            : "(or " + string.Join(" ", edges.Select(e => e.ReachCond)) + ")";

        var mergedEnv = new BlockEnv
        {
            ThisOverride = edges[0].Env.ThisOverride,
            SpecResult = edges[0].Env.SpecResult
        };
        foreach (var e in edges)
        {
            foreach (var kv in e.Env.CaptureAliases) mergedEnv.CaptureAliases[kv.Key] = kv.Value;
        }
        var allSyms = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var e in edges)
        {
            foreach (var k in e.Env.Vars.Keys) allSyms.Add(k);
        }

        foreach (var sym in allSyms)
        {
            var values = edges
                .Where(e => e.Env.Vars.ContainsKey(sym))
                .Select(e => (e.ReachCond, Val: e.Env.Vars[sym]))
                .ToList();

            if (values.Count == 0) continue;
            if (values.All(v => v.Val.Term == values[0].Val.Term))
            {
                mergedEnv.Vars[sym] = values[0].Val;
                continue;
            }

            var type = values[0].Val.Type;
            var iteExpr = values[values.Count - 1].Val.Term;
            for (var i = values.Count - 2; i >= 0; i--)
            {
                iteExpr = $"(ite {values[i].ReachCond} {values[i].Val.Term} {iteExpr})";
            }

            mergedEnv.Vars[sym] = BindSsaValue("phi_" + SmtTypeSystem.SanitizeName(sym.Name), new SmtTypedTerm(iteExpr, type));
        }

        var allCaptures = new HashSet<CaptureId>();
        foreach (var e in edges)
        {
            foreach (var k in e.Env.Captures.Keys) allCaptures.Add(k);
        }

        foreach (var cap in allCaptures)
        {
            var values = edges
                .Where(e => e.Env.Captures.ContainsKey(cap))
                .Select(e => (e.ReachCond, Val: e.Env.Captures[cap]))
                .ToList();

            if (values.Count == 0) continue;
            if (values.All(v => v.Val.Term == values[0].Val.Term))
            {
                mergedEnv.Captures[cap] = values[0].Val;
                continue;
            }

            var type = values[0].Val.Type;
            var iteExpr = values[values.Count - 1].Val.Term;
            for (var i = values.Count - 2; i >= 0; i--)
            {
                iteExpr = $"(ite {values[i].ReachCond} {values[i].Val.Term} {iteExpr})";
            }

            mergedEnv.Captures[cap] = BindSsaValue("phi_cap", new SmtTypedTerm(iteExpr, type));
        }

        return (gamma, mergedEnv);
    }

    private void AssignTarget(IOperation target, SmtTypedTerm value, BlockEnv env, bool updateCaptureAlias = true)
    {
        switch (target)
        {
            case ILocalReferenceOperation lr:
                env.Vars[lr.Local] = _translator.ConvertTo(value, lr.Local.Type);
                break;
            case IParameterReferenceOperation pr:
                env.Vars[pr.Parameter] = _translator.ConvertTo(value, pr.Parameter.Type);
                break;
            case IFlowCaptureReferenceOperation cr:
                env.Captures[cr.Id] = value;
                if (updateCaptureAlias && env.CaptureAliases.TryGetValue(cr.Id, out var aliasedSym))
                {
                    env.Vars[aliasedSym] = _translator.ConvertTo(value, GetSymbolType(aliasedSym));
                }
                break;
            case IMemberReferenceOperation mr when mr.Instance is not null:
                var recv = GetReceiverTerm(mr.Instance, env);
                if (recv is not null && _types.TryGetComposite(recv.Type, out var comp) && comp!.Fields.Count > 0)
                {
                    var fieldTerms = new List<string>(comp.Fields.Count);
                    foreach (var f in comp.Fields)
                    {
                        if (f.Name == mr.Member.Name)
                        {
                            fieldTerms.Add(_translator.ConvertTo(value, f.Type).Term);
                        }
                        else
                        {
                            fieldTerms.Add($"({f.AccessorName} {recv.Term})");
                        }
                    }
                    var updated = BindSsaValue("upd_" + SmtTypeSystem.SanitizeName(mr.Member.Name),
                        new SmtTypedTerm($"({comp.ConstructorName} {string.Join(" ", fieldTerms)})", recv.Type));
                    AssignTarget(mr.Instance, updated, env, updateCaptureAlias: false);
                }
                break;
        }
    }

    private static SmtTypedTerm? GetReceiverTerm(IOperation instance, BlockEnv env) => instance switch
    {
        ILocalReferenceOperation lr => env.Vars.TryGetValue(lr.Local, out var v) ? v : null,
        IParameterReferenceOperation pr => env.Vars.TryGetValue(pr.Parameter, out var v) ? v : new SmtTypedTerm("p_" + SmtTypeSystem.SanitizeName(pr.Parameter.Name), pr.Parameter.Type),
        IFlowCaptureReferenceOperation cr => env.Captures.TryGetValue(cr.Id, out var v) ? v : null,
        _ => null
    };

    private void AssignDeconstruction(IOperation target, SmtTypedTerm rhs, BlockEnv env)
    {
        if (target is ITupleOperation tupleTarget && _types.TryGetComposite(rhs.Type, out var rhsComp))
        {
            for (var i = 0; i < tupleTarget.Elements.Length && i < rhsComp!.Fields.Count; i++)
            {
                var field = rhsComp.Fields[i];
                var elemVal = new SmtTypedTerm($"({field.AccessorName} {rhs.Term})", field.Type);
                var elemTarget = tupleTarget.Elements[i];
                if (elemTarget is IDeclarationExpressionOperation de)
                {
                    AssignTarget(de.Expression, elemVal, env);
                }
                else
                {
                    AssignTarget(elemTarget, elemVal, env);
                }
            }
        }
    }

    private SmtTypedTerm BindSsaValue(string prefix, SmtTypedTerm value, bool isFreshFreeSymbol = false)
    {
        var sort = _types.GetSort(value.Type);
        var name = $"ssa_{++_ssaCounter}_{prefix}";
        _decls.Add($"(declare-const {name} {sort})");
        if (!isFreshFreeSymbol)
        {
            AddAssertion($"(= {name} {value.Term})", "SsaBinding", name, Location.None);
        }
        return new SmtTypedTerm(name, value.Type);
    }

    private void AddAssertion(string smtFormula, string kind, string label, Location loc, string? reason = null)
    {
        if (!_trackProofPremises)
        {
            _assertions.Add($"(assert {smtFormula})");
            return;
        }

        var id = $"p_{++_premiseCounter}_{kind}";
        var (filePath, line) = ExtractFileAndLine(loc);
        _premises.Add(new NamedPremise(id, kind, label, reason, filePath, line, smtFormula));
        _assertions.Add($"(assert (! {smtFormula} :named {id}))");
    }

    private static (string? FilePath, int Line) ExtractFileAndLine(Location loc)
    {
        if (loc == Location.None || !loc.IsInSource)
        {
            return (null, 0);
        }

        var span = loc.GetMappedLineSpan();
        var rawPath = span.Path ?? loc.SourceTree?.FilePath;
        var file = string.IsNullOrEmpty(rawPath) ? null : rawPath;
        return (file, span.StartLinePosition.Line + 1);
    }

    private static IReadOnlyList<string> FlattenConjuncts(string term)
    {
        var list = new List<string>();
        Collect(term);
        return list;

        void Collect(string t)
        {
            var s = t.Trim();
            if (string.IsNullOrEmpty(s) || s == "true") return;
            if (!s.StartsWith("(and ", StringComparison.Ordinal) || !s.EndsWith(")", StringComparison.Ordinal))
            {
                list.Add(s);
                return;
            }

            var i = 5;
            var limit = s.Length - 1;
            var children = new List<string>();
            while (i < limit)
            {
                while (i < limit && char.IsWhiteSpace(s[i])) i++;
                if (i >= limit) break;
                if (s[i] == '(')
                {
                    var start = i;
                    var depth = 0;
                    var inStr = false;
                    while (i < limit)
                    {
                        var ch = s[i];
                        if (inStr)
                        {
                            if (ch == '"' && s[i - 1] != '\\') inStr = false;
                        }
                        else if (ch == '"') inStr = true;
                        else if (ch == '(') depth++;
                        else if (ch == ')')
                        {
                            depth--;
                            if (depth == 0) { i++; break; }
                        }
                        i++;
                    }
                    children.Add(s.Substring(start, i - start));
                }
                else
                {
                    var start = i;
                    while (i < limit && !char.IsWhiteSpace(s[i])) i++;
                    children.Add(s.Substring(start, i - start));
                }
            }

            foreach (var c in children) Collect(c);
        }
    }

    private void AddObligation(string kind, string description, Location location, string gamma, string goalTerm, string? reason = null)
    {
        var preamble = string.Join("\n", _types.Declarations.Concat(_decls).Concat(_assertions));
        _obligations.Add(new ProofObligation(kind, description, location, gamma, goalTerm, preamble, _trackedParams.ToList(), reason));
    }

    private static string? ExtractConstantStringArg(IOperation argOp)
    {
        var cur = argOp;
        while (cur is IConversionOperation conv)
        {
            cur = conv.Operand;
        }
        return cur.ConstantValue.HasValue ? cur.ConstantValue.Value as string : null;
    }

    private static string CombineAnd(string a, string b)
    {
        if (a == "true") return b;
        if (b == "true") return a;
        return $"(and {a} {b})";
    }

    private static bool HasCheckedArithmetic(IMethodSymbol method)
    {
        if (VerifiableSymbolHelpers.HasAttribute(method, VerifiableSymbolHelpers.CheckedArithmeticAttr))
            return true;
        for (var cur = method.ContainingType; cur is not null; cur = cur.ContainingType)
        {
            if (VerifiableSymbolHelpers.HasAttribute(cur, VerifiableSymbolHelpers.CheckedArithmeticAttr))
                return true;
        }
        return method.ContainingAssembly is not null &&
               VerifiableSymbolHelpers.HasAttribute(method.ContainingAssembly, VerifiableSymbolHelpers.CheckedArithmeticAttr);
    }

    private static HashSet<(BasicBlock Src, BasicBlock Dst)> FindBackEdges(ControlFlowGraph cfg)
    {
        var backEdges = new HashSet<(BasicBlock, BasicBlock)>();
        var visited = new HashSet<BasicBlock>();
        var onStack = new HashSet<BasicBlock>();

        void Dfs(BasicBlock u)
        {
            visited.Add(u);
            onStack.Add(u);
            foreach (var v in SuccessorsOf(u))
            {
                if (onStack.Contains(v))
                {
                    backEdges.Add((u, v));
                }
                else if (!visited.Contains(v))
                {
                    Dfs(v);
                }
            }
            onStack.Remove(u);
        }

        Dfs(cfg.Blocks[0]);
        return backEdges;
    }

    private static HashSet<BasicBlock> CollectNaturalLoopBlocks(BasicBlock header, List<BasicBlock> tailNodes, ControlFlowGraph cfg)
    {
        var body = new HashSet<BasicBlock> { header };
        var stack = new Stack<BasicBlock>();
        foreach (var t in tailNodes)
        {
            if (body.Add(t)) stack.Push(t);
        }

        var preds = new Dictionary<BasicBlock, List<BasicBlock>>();
        foreach (var b in cfg.Blocks) preds[b] = new List<BasicBlock>();
        foreach (var b in cfg.Blocks)
        {
            foreach (var s in SuccessorsOf(b))
            {
                preds[s].Add(b);
            }
        }

        while (stack.Count > 0)
        {
            var m = stack.Pop();
            foreach (var p in preds[m])
            {
                if (body.Add(p)) stack.Push(p);
            }
        }

        return body;
    }

    private static List<BasicBlock> ComputeRpoIgnoringBackEdges(ControlFlowGraph cfg, HashSet<(BasicBlock Src, BasicBlock Dst)> backEdges)
    {
        var visited = new HashSet<BasicBlock>();
        var postOrder = new List<BasicBlock>();

        void Dfs(BasicBlock u)
        {
            visited.Add(u);
            foreach (var v in SuccessorsOf(u))
            {
                if (!backEdges.Contains((u, v)) && !visited.Contains(v))
                {
                    Dfs(v);
                }
            }
            postOrder.Add(u);
        }

        Dfs(cfg.Blocks[0]);
        postOrder.Reverse();
        return postOrder;
    }

    private static List<BasicBlock> ComputeSubgraphRpo(BasicBlock header, HashSet<BasicBlock> nodes, HashSet<(BasicBlock Src, BasicBlock Dst)> backEdges)
    {
        var visited = new HashSet<BasicBlock>();
        var postOrder = new List<BasicBlock>();

        void Dfs(BasicBlock u)
        {
            visited.Add(u);
            foreach (var v in SuccessorsOf(u))
            {
                if (nodes.Contains(v) && !backEdges.Contains((u, v)) && !visited.Contains(v))
                {
                    Dfs(v);
                }
            }
            postOrder.Add(u);
        }

        Dfs(header);
        postOrder.Reverse();
        return postOrder;
    }

    private static IEnumerable<BasicBlock> SuccessorsOf(BasicBlock b)
    {
        if (b.ConditionalSuccessor?.Destination is { } c) yield return c;
        if (b.FallThroughSuccessor?.Destination is { } f && b.FallThroughSuccessor.Semantics == ControlFlowBranchSemantics.Regular) yield return f;
    }

    private static HashSet<ISymbol> CollectModifiedSymbols(HashSet<BasicBlock> loopBlocks)
    {
        var captureAliases = new Dictionary<CaptureId, ISymbol>();
        foreach (var b in loopBlocks)
        {
            foreach (var op in b.Operations)
            {
                foreach (var cap in op.DescendantsAndSelf().OfType<IFlowCaptureOperation>())
                {
                    if (cap.Value is ILocalReferenceOperation lr) captureAliases[cap.Id] = lr.Local;
                    else if (cap.Value is IParameterReferenceOperation pr) captureAliases[cap.Id] = pr.Parameter;
                }
            }
        }

        var set = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var b in loopBlocks)
        {
            foreach (var op in b.Operations)
            {
                foreach (var desc in op.DescendantsAndSelf())
                {
                    switch (desc)
                    {
                        case IVariableDeclaratorOperation vd:
                            set.Add(vd.Symbol);
                            break;
                        case ISimpleAssignmentOperation sa:
                            AddAssignedSym(sa.Target, captureAliases, set);
                            break;
                        case ICompoundAssignmentOperation ca:
                            AddAssignedSym(ca.Target, captureAliases, set);
                            break;
                        case IIncrementOrDecrementOperation inc:
                            AddAssignedSym(inc.Target, captureAliases, set);
                            break;
                    }
                }
            }
        }
        return set;
    }

    private static void AddAssignedSym(IOperation target, Dictionary<CaptureId, ISymbol> captureAliases, HashSet<ISymbol> set)
    {
        if (target is ILocalReferenceOperation lr) set.Add(lr.Local);
        else if (target is IParameterReferenceOperation pr) set.Add(pr.Parameter);
        else if (target is IFlowCaptureReferenceOperation cr && captureAliases.TryGetValue(cr.Id, out var sym)) set.Add(sym);
    }

    private static ITypeSymbol GetSymbolType(ISymbol sym) => sym switch
    {
        ILocalSymbol l => l.Type,
        IParameterSymbol p => p.Type,
        _ => throw new InvalidOperationException($"Unexpected symbol kind {sym.Kind}")
    };
}
