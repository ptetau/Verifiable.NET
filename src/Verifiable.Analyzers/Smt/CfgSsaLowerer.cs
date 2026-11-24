using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
    IReadOnlyList<(string ParamName, string SmtName)> TrackedParameters);

internal sealed record LoweringOutcome(
    IReadOnlyList<ProofObligation> Obligations,
    IReadOnlyList<(string Contract, Location Location, string Error)> Errors);

internal sealed class CfgSsaLowerer
{
    private readonly Compilation _compilation;
    private readonly IMethodSymbol _method;
    private readonly SmtTypeSystem _types;
    private readonly ContractExpressionTranslator _translator;

    private readonly List<string> _decls = new();
    private readonly List<string> _assertions = new();
    private readonly List<ProofObligation> _obligations = new();
    private readonly List<(string Contract, Location Location, string Error)> _errors = new();
    private readonly List<(string ParamName, string SmtName)> _trackedParams = new();
    private readonly HashSet<string> _emittedOnDemandKeys = new(StringComparer.Ordinal);

    private int _ssaCounter;

    private sealed class BlockEnv
    {
        public Dictionary<ISymbol, SmtTypedTerm> Vars { get; } = new(SymbolEqualityComparer.Default);
        public Dictionary<CaptureId, SmtTypedTerm> Captures { get; } = new();
        public Dictionary<CaptureId, ISymbol> CaptureAliases { get; } = new();

        public BlockEnv Clone()
        {
            var copy = new BlockEnv();
            foreach (var kv in Vars) copy.Vars[kv.Key] = kv.Value;
            foreach (var kv in Captures) copy.Captures[kv.Key] = kv.Value;
            foreach (var kv in CaptureAliases) copy.CaptureAliases[kv.Key] = kv.Value;
            return copy;
        }
    }

    private sealed record IncomingEdge(BasicBlock From, string ReachCond, BlockEnv Env);

    public CfgSsaLowerer(Compilation compilation, IMethodSymbol method, SyntaxTree? contextTree)
    {
        _compilation = compilation;
        _method = method;
        var useBitVec = HasCheckedArithmetic(method);
        _types = new SmtTypeSystem(useBitVec);
        _translator = new ContractExpressionTranslator(compilation, _types, method.ContainingType, contextTree);
    }

    public LoweringOutcome Lower(ControlFlowGraph cfg)
    {
        var methodLoc = _method.Locations.FirstOrDefault() ?? Location.None;
        var entryEnv = new BlockEnv();
        var paramTranslatorEnv = new Dictionary<string, SmtTypedTerm>(StringComparer.Ordinal);

        if (!_method.IsStatic && _method.ContainingType is { } recvType)
        {
            var sort = _types.GetSort(recvType);
            const string thisSmt = "p_this";
            _decls.Add($"(declare-const {thisSmt} {sort})");
            _trackedParams.Add(("this", thisSmt));
            var thisTerm = new SmtTypedTerm(thisSmt, recvType);
            paramTranslatorEnv["this"] = thisTerm;
            EmitTypeAssumptions(thisTerm, methodLoc);
        }

        foreach (var p in _method.Parameters)
        {
            var sort = _types.GetSort(p.Type);
            var smtName = "p_" + SmtTypeSystem.SanitizeName(p.Name);
            _decls.Add($"(declare-const {smtName} {sort})");
            _trackedParams.Add((p.Name, smtName));
            var pTerm = new SmtTypedTerm(smtName, p.Type);
            entryEnv.Vars[p] = pTerm;
            paramTranslatorEnv[p.Name] = pTerm;
            EmitTypeAssumptions(pTerm, p.Locations.FirstOrDefault() ?? methodLoc);
        }

        // Method [Requires] entry assumptions
        foreach (var req in GetAttributeStrings(_method, VerifiableSymbolHelpers.RequiresAttr))
        {
            try
            {
                var reqTerm = _translator.TranslateContractString(req, paramTranslatorEnv, _compilation.GetSpecialType(SpecialType.System_Boolean));
                _assertions.Add($"(assert {reqTerm.Term})");
            }
            catch (ContractLoweringException ex)
            {
                _errors.Add((req, methodLoc, ex.Message));
            }
        }

        if (_errors.Count > 0 || cfg.Blocks.Length == 0)
        {
            return new LoweringOutcome(_obligations, _errors);
        }

        // Analyze loops vs acyclic CFG
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

        var blockGammas = new Dictionary<BasicBlock, string>();
        var blockPostEnvs = new Dictionary<BasicBlock, BlockEnv>();

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
                LowerNaturalLoop(block, loopBody, edges, cfg, backEdges, incoming, blockGammas, blockPostEnvs, paramTranslatorEnv);
                continue;
            }

            var (gamma, env) = MergeIncomingEdges(edges);
            blockGammas[block] = gamma;

            LowerBasicBlock(block, gamma, env, paramTranslatorEnv, incoming);
            blockPostEnvs[block] = env;
        }

        return new LoweringOutcome(_obligations, _errors);
    }

    private void LowerNaturalLoop(
        BasicBlock header,
        HashSet<BasicBlock> loopBody,
        List<IncomingEdge> preEdges,
        ControlFlowGraph cfg,
        HashSet<(BasicBlock Src, BasicBlock Dst)> backEdges,
        Dictionary<BasicBlock, List<IncomingEdge>> incoming,
        Dictionary<BasicBlock, string> blockGammas,
        Dictionary<BasicBlock, BlockEnv> blockPostEnvs,
        IReadOnlyDictionary<string, SmtTypedTerm> paramEnv)
    {
        var (preGamma, preEnv) = MergeIncomingEdges(preEdges);
        var headerLoc = header.BranchValue?.Syntax.GetLocation() ?? _method.Locations.FirstOrDefault() ?? Location.None;

        var loopInvs = GetAttributeStrings(_method, VerifiableSymbolHelpers.LoopInvariantAttr);
        var decreasesExprs = GetAttributeStrings(_method, VerifiableSymbolHelpers.DecreasesAttr);

        // 1. Check LoopInvariant entry and Decreases >= 0 at pre-loop state
        foreach (var inv in loopInvs)
        {
            try
            {
                var tEnv = BuildTranslatorEnv(preEnv, paramEnv);
                var invTerm = _translator.TranslateContractString(inv, tEnv, _compilation.GetSpecialType(SpecialType.System_Boolean));
                AddObligation("LoopInvariantEntry", $"[LoopInvariant(\"{inv}\")] on entry", headerLoc, preGamma, invTerm.Term);
            }
            catch (ContractLoweringException ex)
            {
                _errors.Add((inv, headerLoc, ex.Message));
            }
        }

        foreach (var dec in decreasesExprs)
        {
            try
            {
                var tEnv = BuildTranslatorEnv(preEnv, paramEnv);
                var decTerm = _translator.TranslateContractString(dec, tEnv, _compilation.GetSpecialType(SpecialType.System_Int32));
                var zero = _translator.FormatIntLiteral(0, 32);
                var geOp = ContractExpressionTranslator.IsBitVecSort(_types.GetSort(decTerm.Type)) ? "bvsge" : ">=";
                AddObligation("LoopDecreasesEntry", $"[Decreases(\"{dec}\")] >= 0 on entry", headerLoc, preGamma, $"({geOp} {decTerm.Term} {zero})");
            }
            catch (ContractLoweringException ex)
            {
                _errors.Add((dec, headerLoc, ex.Message));
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

        // Build invariant assumption in havoced state
        var invAssumptions = new List<string>();
        var loopTransEnv = BuildTranslatorEnv(loopEnv, paramEnv);
        foreach (var inv in loopInvs)
        {
            try
            {
                var invTerm = _translator.TranslateContractString(inv, loopTransEnv, _compilation.GetSpecialType(SpecialType.System_Boolean));
                invAssumptions.Add(invTerm.Term);
            }
            catch (ContractLoweringException ex)
            {
                _errors.Add((inv, headerLoc, ex.Message));
            }
        }

        var decBeforeTerms = new List<(string Expr, SmtTypedTerm Term)>();
        foreach (var dec in decreasesExprs)
        {
            try
            {
                var decTerm = _translator.TranslateContractString(dec, loopTransEnv, _compilation.GetSpecialType(SpecialType.System_Int32));
                var zero = _translator.FormatIntLiteral(0, 32);
                var geOp = ContractExpressionTranslator.IsBitVecSort(_types.GetSort(decTerm.Type)) ? "bvsge" : ">=";
                invAssumptions.Add($"({geOp} {decTerm.Term} {zero})");
                decBeforeTerms.Add((dec, decTerm));
            }
            catch (ContractLoweringException ex)
            {
                _errors.Add((dec, headerLoc, ex.Message));
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
            LowerOperation(op, loopGamma, headerWorkEnv, paramEnv);
        }

        var condDest = header.ConditionalSuccessor?.Destination;
        var fallDest = header.FallThroughSuccessor?.Destination;
        string? condBranchCond = null;
        string? fallBranchCond = null;

        if (header.BranchValue is { } bv && header.ConditionalSuccessor is not null)
        {
            var condVal = LowerExpression(bv, loopGamma, headerWorkEnv, paramEnv);
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

        // Route exit edges outside loopBody and body edges inside loopBody
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

        // Simulate loop body blocks in topological order (excluding header back-edge)
        var bodyOrder = ComputeSubgraphRpo(header, loopBody, backEdges);
        foreach (var b in bodyOrder)
        {
            if (ReferenceEquals(b, header)) continue;
            var bEdges = bodyIncoming[b];
            if (bEdges.Count == 0) continue;

            var (bGamma, bEnv) = MergeIncomingEdges(bEdges);
            blockGammas[b] = bGamma;

            // Route internal vs back-edge vs external exit
            foreach (var op in b.Operations)
            {
                LowerOperation(op, bGamma, bEnv, paramEnv);
            }

            if (b.ConditionalSuccessor?.Destination is { } bCondDest)
            {
                var bvTerm = b.BranchValue is not null ? LowerExpression(b.BranchValue, bGamma, bEnv, paramEnv) : null;
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

                DispatchLoopBodyEdge(b, bCondDest, CombineAnd(bGamma, cTerm), bEnv, header, loopBody, bodyIncoming, incoming, loopInvs, decBeforeTerms, paramEnv, headerLoc);
                if (b.FallThroughSuccessor?.Destination is { } bFallDest)
                {
                    DispatchLoopBodyEdge(b, bFallDest, CombineAnd(bGamma, fTerm), bEnv, header, loopBody, bodyIncoming, incoming, loopInvs, decBeforeTerms, paramEnv, headerLoc);
                }
            }
            else if (b.FallThroughSuccessor?.Semantics == ControlFlowBranchSemantics.Return)
            {
                HandleReturnBranch(b, bGamma, bEnv, paramEnv);
            }
            else if (b.FallThroughSuccessor?.Destination is { } bFallOnly && b.FallThroughSuccessor.Semantics == ControlFlowBranchSemantics.Regular)
            {
                DispatchLoopBodyEdge(b, bFallOnly, bGamma, bEnv, header, loopBody, bodyIncoming, incoming, loopInvs, decBeforeTerms, paramEnv, headerLoc);
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
        IReadOnlyList<string> loopInvs,
        IReadOnlyList<(string Expr, SmtTypedTerm Term)> decBeforeTerms,
        IReadOnlyDictionary<string, SmtTypedTerm> paramEnv,
        Location headerLoc)
    {
        if (ReferenceEquals(dst, header))
        {
            // Back-edge reached: assert inductive step & decreases
            var postTransEnv = BuildTranslatorEnv(env, paramEnv);
            foreach (var inv in loopInvs)
            {
                try
                {
                    var invTerm = _translator.TranslateContractString(inv, postTransEnv, _compilation.GetSpecialType(SpecialType.System_Boolean));
                    AddObligation("LoopInvariantStep", $"[LoopInvariant(\"{inv}\")] preservation", headerLoc, edgeGamma, invTerm.Term);
                }
                catch (ContractLoweringException ex)
                {
                    _errors.Add((inv, headerLoc, ex.Message));
                }
            }

            foreach (var (decExpr, beforeTerm) in decBeforeTerms)
            {
                try
                {
                    var afterTerm = _translator.TranslateContractString(decExpr, postTransEnv, _compilation.GetSpecialType(SpecialType.System_Int32));
                    var zero = _translator.FormatIntLiteral(0, 32);
                    var isBv = ContractExpressionTranslator.IsBitVecSort(_types.GetSort(afterTerm.Type));
                    var geOp = isBv ? "bvsge" : ">=";
                    var ltOp = isBv ? "bvslt" : "<";
                    AddObligation("LoopDecreasesStep", $"[Decreases(\"{decExpr}\")] decreases", headerLoc, edgeGamma,
                        $"(and ({geOp} {afterTerm.Term} {zero}) ({ltOp} {afterTerm.Term} {beforeTerm.Term}))");
                }
                catch (ContractLoweringException ex)
                {
                    _errors.Add((decExpr, headerLoc, ex.Message));
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
        IReadOnlyDictionary<string, SmtTypedTerm> paramEnv,
        Dictionary<BasicBlock, List<IncomingEdge>> incoming)
    {
        foreach (var op in block.Operations)
        {
            LowerOperation(op, gamma, env, paramEnv);
        }

        if (block.ConditionalSuccessor?.Destination is { } condDest)
        {
            string condTerm = "true", fallTerm = "true";
            if (block.BranchValue is { } bv)
            {
                var c = LowerExpression(bv, gamma, env, paramEnv);
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
            HandleReturnBranch(block, gamma, env, paramEnv);
            return;
        }

        if (block.FallThroughSuccessor?.Destination is { Kind: not BasicBlockKind.Exit } next &&
            block.FallThroughSuccessor.Semantics == ControlFlowBranchSemantics.Regular)
        {
            incoming[next].Add(new IncomingEdge(block, gamma, env.Clone()));
        }
    }

    private void HandleReturnBranch(
        BasicBlock block,
        string gamma,
        BlockEnv env,
        IReadOnlyDictionary<string, SmtTypedTerm> paramEnv)
    {
        if (block.BranchValue is null)
        {
            return;
        }

        var retTerm = LowerExpression(block.BranchValue, gamma, env, paramEnv);
        var retLoc = block.BranchValue.Syntax.GetLocation();
        var retTransEnv = BuildTranslatorEnv(env, paramEnv);
        retTransEnv["result"] = retTerm;

        foreach (var ens in GetAttributeStrings(_method, VerifiableSymbolHelpers.EnsuresAttr))
        {
            try
            {
                var goal = _translator.TranslateContractString(ens, retTransEnv, _compilation.GetSpecialType(SpecialType.System_Boolean));
                AddObligation("Postcondition", $"[Ensures(\"{ens}\")]", retLoc, gamma, goal.Term);
            }
            catch (ContractLoweringException ex)
            {
                _errors.Add((ens, retLoc, ex.Message));
            }
        }

        // Check return type [Invariant]s (guarding Result<T, E> by IsSuccess)
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
        foreach (var inv in SmtTypeSystem.GetInvariants(value.Type))
        {
            try
            {
                var env = new Dictionary<string, SmtTypedTerm>(StringComparer.Ordinal) { ["this"] = value };
                var goal = _translator.TranslateContractString(inv, env, _compilation.GetSpecialType(SpecialType.System_Boolean));
                AddObligation("TypeInvariant", $"[Invariant(\"{inv}\")] on {value.Type.Name}", loc, gamma, goal.Term);
            }
            catch (ContractLoweringException ex)
            {
                _errors.Add((inv, loc, ex.Message));
            }
        }

        if (_types.TryGetComposite(value.Type, out var comp) && !SmtTypeSystem.TryGetRefinedUnderlying(value.Type, out _, out _))
        {
            foreach (var f in comp!.Fields)
            {
                if (SmtTypeSystem.GetInvariants(f.Type).Count > 0)
                {
                    EmitValueInvariantObligations(new SmtTypedTerm($"({f.AccessorName} {value.Term})", f.Type), gamma, loc);
                }
            }
        }
    }

    private void LowerOperation(IOperation op, string gamma, BlockEnv env, IReadOnlyDictionary<string, SmtTypedTerm> paramEnv)
    {
        switch (op)
        {
            case IExpressionStatementOperation es:
                _ = LowerExpression(es.Operation, gamma, env, paramEnv);
                break;

            case IVariableDeclarationGroupOperation dg:
                foreach (var decl in dg.Declarations)
                {
                    foreach (var d in decl.Declarators)
                    {
                        LowerVariableDeclarator(d, gamma, env, paramEnv);
                    }
                }
                break;

            case IVariableDeclarationOperation sd:
                foreach (var d in sd.Declarators)
                {
                    LowerVariableDeclarator(d, gamma, env, paramEnv);
                }
                break;

            case IVariableDeclaratorOperation vd:
                LowerVariableDeclarator(vd, gamma, env, paramEnv);
                break;

            case IReturnOperation ret when ret.ReturnedValue is not null:
                var retVal = LowerExpression(ret.ReturnedValue, gamma, env, paramEnv);
                var retEnv = BuildTranslatorEnv(env, paramEnv);
                retEnv["result"] = retVal;
                foreach (var ens in GetAttributeStrings(_method, VerifiableSymbolHelpers.EnsuresAttr))
                {
                    try
                    {
                        var goal = _translator.TranslateContractString(ens, retEnv, _compilation.GetSpecialType(SpecialType.System_Boolean));
                        AddObligation("Postcondition", $"[Ensures(\"{ens}\")]", ret.Syntax.GetLocation(), gamma, goal.Term);
                    }
                    catch (ContractLoweringException ex)
                    {
                        _errors.Add((ens, ret.Syntax.GetLocation(), ex.Message));
                    }
                }
                EmitReturnInvariantObligations(retVal, gamma, ret.Syntax.GetLocation());
                break;

            default:
                _ = LowerExpression(op, gamma, env, paramEnv);
                break;
        }
    }

    private void LowerVariableDeclarator(IVariableDeclaratorOperation vd, string gamma, BlockEnv env, IReadOnlyDictionary<string, SmtTypedTerm> paramEnv)
    {
        if (vd.Initializer is not null)
        {
            var rhs = LowerExpression(vd.Initializer.Value, gamma, env, paramEnv);
            var converted = _translator.ConvertTo(rhs, vd.Symbol.Type);
            var ssa = BindSsaValue(vd.Symbol.Name, converted);
            env.Vars[vd.Symbol] = ssa;
        }
        else
        {
            env.Vars[vd.Symbol] = new SmtTypedTerm(_types.GetDefaultTerm(vd.Symbol.Type), vd.Symbol.Type);
        }
    }

    private SmtTypedTerm LowerExpression(IOperation op, string gamma, BlockEnv env, IReadOnlyDictionary<string, SmtTypedTerm> paramEnv)
    {
        switch (op)
        {
            case IConversionOperation conv:
                if (conv.Operand is ILiteralOperation litNull && litNull.ConstantValue.HasValue && litNull.ConstantValue.Value is null)
                {
                    if (conv.Type is not null)
                    {
                        if (_types.TryGetNullableInfo(conv.Type, out _, out var optId, out _))
                            return new SmtTypedTerm($"opt-none-{optId}", conv.Type);
                        if (conv.Type.SpecialType == SpecialType.System_String)
                            return new SmtTypedTerm("str-null", conv.Type);
                        return new SmtTypedTerm(_types.GetDefaultTerm(conv.Type), conv.Type);
                    }
                }
                var inner = LowerExpression(conv.Operand, gamma, env, paramEnv);
                return conv.Type is not null ? _translator.ConvertTo(inner, conv.Type) : inner;

            case IParenthesizedOperation paren:
                return LowerExpression(paren.Operand, gamma, env, paramEnv);

            case ILocalReferenceOperation lr:
                if (env.Vars.TryGetValue(lr.Local, out var lVal)) return lVal;
                return new SmtTypedTerm(_types.GetDefaultTerm(lr.Local.Type), lr.Local.Type);

            case IParameterReferenceOperation pr:
                if (env.Vars.TryGetValue(pr.Parameter, out var pVal)) return pVal;
                return new SmtTypedTerm("p_" + SmtTypeSystem.SanitizeName(pr.Parameter.Name), pr.Parameter.Type);

            case IInstanceReferenceOperation inst when inst.ReferenceKind == InstanceReferenceKind.ContainingTypeInstance:
                if (paramEnv.TryGetValue("this", out var thisVal)) return thisVal;
                return new SmtTypedTerm(_types.GetDefaultTerm(op.Type!), op.Type!);

            case IFlowCaptureOperation cap:
                var capVal = LowerExpression(cap.Value, gamma, env, paramEnv);
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
                var saRhs = LowerExpression(sa.Value, gamma, env, paramEnv);
                AssignTarget(sa.Target, saRhs, env);
                return saRhs;

            case IDeconstructionAssignmentOperation da:
                var daRhs = LowerExpression(da.Value, gamma, env, paramEnv);
                AssignDeconstruction(da.Target, daRhs, env);
                return daRhs;

            case ICompoundAssignmentOperation ca:
                var curTarget = LowerExpression(ca.Target, gamma, env, paramEnv);
                var rhsVal = LowerExpression(ca.Value, gamma, env, paramEnv);
                var combined = LowerBinaryOperator(ca.OperatorKind, curTarget, rhsVal, ca.Type!, gamma, ca.Syntax.GetLocation());
                var combinedSsa = BindSsaValue("assign", combined);
                AssignTarget(ca.Target, combinedSsa, env);
                return combinedSsa;

            case IIncrementOrDecrementOperation inc:
                var prev = LowerExpression(inc.Target, gamma, env, paramEnv);
                var one = new SmtTypedTerm(_translator.FormatIntLiteral(1, inc.Type!.SpecialType == SpecialType.System_Int64 ? 64 : 32), inc.Type);
                var binKind = inc.Kind == OperationKind.Increment ? BinaryOperatorKind.Add : BinaryOperatorKind.Subtract;
                var next = LowerBinaryOperator(binKind, prev, one, inc.Type, gamma, inc.Syntax.GetLocation());
                var nextSsa = BindSsaValue("inc", next);
                AssignTarget(inc.Target, nextSsa, env);
                return inc.IsPostfix ? prev : nextSsa;

            case IUnaryOperation un:
                var uOp = LowerExpression(un.Operand, gamma, env, paramEnv);
                if (un.OperatorKind == UnaryOperatorKind.Not)
                    return new SmtTypedTerm($"(not {uOp.Term})", _compilation.GetSpecialType(SpecialType.System_Boolean));
                if (un.OperatorKind == UnaryOperatorKind.Minus)
                {
                    var sort = _types.GetSort(uOp.Type);
                    if (_types.UseBitVec && sort is "(_ BitVec 32)" or "(_ BitVec 64)")
                    {
                        var minVal = sort == "(_ BitVec 32)" ? "(_ bv2147483648 32)" : "(_ bv9223372036854775808 64)";
                        AddObligation("Overflow", $"Unary negation overflow check on '{un.Syntax}'", un.Syntax.GetLocation(), gamma, $"(not (= {uOp.Term} {minVal}))");
                        return new SmtTypedTerm($"(bvneg {uOp.Term})", uOp.Type);
                    }
                    return new SmtTypedTerm($"(- {uOp.Term})", uOp.Type);
                }
                return uOp;

            case IBinaryOperation bin:
                if (bin.OperatorKind == BinaryOperatorKind.ConditionalAnd)
                {
                    var lAnd = LowerExpression(bin.LeftOperand, gamma, env, paramEnv);
                    var rAnd = LowerExpression(bin.RightOperand, CombineAnd(gamma, lAnd.Term), env, paramEnv);
                    return new SmtTypedTerm($"(and {lAnd.Term} {rAnd.Term})", _compilation.GetSpecialType(SpecialType.System_Boolean));
                }
                if (bin.OperatorKind == BinaryOperatorKind.ConditionalOr)
                {
                    var lOr = LowerExpression(bin.LeftOperand, gamma, env, paramEnv);
                    var rOr = LowerExpression(bin.RightOperand, CombineAnd(gamma, $"(not {lOr.Term})"), env, paramEnv);
                    return new SmtTypedTerm($"(or {lOr.Term} {rOr.Term})", _compilation.GetSpecialType(SpecialType.System_Boolean));
                }
                var lhs = LowerExpression(bin.LeftOperand, gamma, env, paramEnv);
                var rhs = LowerExpression(bin.RightOperand, gamma, env, paramEnv);
                return LowerBinaryOperator(bin.OperatorKind, lhs, rhs, bin.Type!, gamma, bin.Syntax.GetLocation());

            case IConditionalOperation cond:
                var cTerm = LowerExpression(cond.Condition, gamma, env, paramEnv);
                var tTerm = LowerExpression(cond.WhenTrue, CombineAnd(gamma, cTerm.Term), env, paramEnv);
                var fTerm = LowerExpression(cond.WhenFalse!, CombineAnd(gamma, $"(not {cTerm.Term})"), env, paramEnv);
                var (pt, pf, uType) = _translator.UnifyForComparison(tTerm, fTerm);
                return new SmtTypedTerm($"(ite {cTerm.Term} {pt} {pf})", uType);

            case IIsPatternOperation isPat:
                var subj = LowerExpression(isPat.Value, gamma, env, paramEnv);
                return LowerPatternOp(subj, isPat.Pattern, gamma, env, paramEnv);

            case IPropertyReferenceOperation propRef:
                return LowerPropertyReference(propRef, gamma, env, paramEnv);

            case IFieldReferenceOperation fieldRef:
                return LowerFieldReference(fieldRef, gamma, env, paramEnv);

            case IWithOperation withOp:
                return LowerWithOperation(withOp, gamma, env, paramEnv);

            case IObjectCreationOperation objCreate:
                return LowerObjectCreation(objCreate, gamma, env, paramEnv);

            case ITupleOperation tuple:
                return LowerTuple(tuple, gamma, env, paramEnv);

            case IInvocationOperation inv:
                return LowerInvocation(inv, gamma, env, paramEnv);

            case ICollectionExpressionOperation colExpr:
                return LowerCollectionExpression(colExpr, gamma, env, paramEnv);

            default:
                if (op.Type is not null)
                {
                    return BindSsaValue("unk", new SmtTypedTerm(_types.GetDefaultTerm(op.Type), op.Type), isFreshFreeSymbol: true);
                }
                return new SmtTypedTerm("0", _compilation.GetSpecialType(SpecialType.System_Int32));
        }
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
        if (type.TypeKind == TypeKind.Enum)
        {
            var num = Convert.ToInt64(val, CultureInfo.InvariantCulture);
            return new SmtTypedTerm(num < 0 ? $"(- {-num})" : num.ToString(CultureInfo.InvariantCulture), type);
        }

        return new SmtTypedTerm(Convert.ToInt64(val, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture), type);
    }

    private SmtTypedTerm LowerBinaryOperator(
        BinaryOperatorKind kind,
        SmtTypedTerm left,
        SmtTypedTerm right,
        ITypeSymbol resultType,
        string gamma,
        Location loc)
    {
        if (kind == BinaryOperatorKind.Add &&
            (resultType.SpecialType == SpecialType.System_String ||
             left.Type.SpecialType == SpecialType.System_String ||
             right.Type.SpecialType == SpecialType.System_String))
        {
            var strType = _compilation.GetSpecialType(SpecialType.System_String);
            var strSsa = BindSsaValue("strcat", new SmtTypedTerm("str-null", strType), isFreshFreeSymbol: true);
            _assertions.Add($"(assert (not (= {strSsa.Term} str-null)))");
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

        if (isBv && kind is BinaryOperatorKind.Add or BinaryOperatorKind.Subtract or BinaryOperatorKind.Multiply)
        {
            EmitBitVecOverflowObligation(kind, lTerm, rTerm, sort, gamma, loc);
        }

        if (kind is BinaryOperatorKind.Divide or BinaryOperatorKind.Remainder && !isBv && sort != "Real")
        {
            AddObligation("DivisionByZero", "Divisor must be non-zero", loc, gamma, $"(not (= {rTerm} 0))");
        }
        else if (kind == BinaryOperatorKind.Divide && sort == "Real")
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

    private SmtTypedTerm LowerPatternOp(SmtTypedTerm subject, IPatternOperation pattern, string gamma, BlockEnv env, IReadOnlyDictionary<string, SmtTypedTerm> paramEnv)
    {
        var boolType = _compilation.GetSpecialType(SpecialType.System_Boolean);
        switch (pattern)
        {
            case IConstantPatternOperation cp:
                var val = LowerExpression(cp.Value, gamma, env, paramEnv);
                var (l, r, _) = _translator.UnifyForComparison(subject, val);
                return new SmtTypedTerm($"(= {l} {r})", boolType);

            case INegatedPatternOperation np:
                var innerNeg = LowerPatternOp(subject, np.Pattern, gamma, env, paramEnv);
                return new SmtTypedTerm($"(not {innerNeg.Term})", boolType);

            case IBinaryPatternOperation bp:
                var left = LowerPatternOp(subject, bp.LeftPattern, gamma, env, paramEnv);
                var right = LowerPatternOp(subject, bp.RightPattern, gamma, env, paramEnv);
                var op = bp.OperatorKind == BinaryOperatorKind.And ? "and" : "or";
                return new SmtTypedTerm($"({op} {left.Term} {right.Term})", boolType);

            case IRelationalPatternOperation rp:
                var rVal = LowerExpression(rp.Value, gamma, env, paramEnv);
                return LowerBinaryOperator(rp.OperatorKind, subject, rVal, boolType, gamma, pattern.Syntax.GetLocation());

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

            case IDiscardPatternOperation:
                return new SmtTypedTerm("true", boolType);

            default:
                return new SmtTypedTerm("true", boolType);
        }
    }

    private SmtTypedTerm LowerPropertyReference(IPropertyReferenceOperation propRef, string gamma, BlockEnv env, IReadOnlyDictionary<string, SmtTypedTerm> paramEnv)
    {
        var prop = propRef.Property;

        // Indexer access: dict[k] or list[i]
        if (prop.IsIndexer && propRef.Instance is not null && propRef.Arguments.Length == 1)
        {
            var recv = LowerExpression(propRef.Instance, gamma, env, paramEnv);
            var arg = LowerExpression(propRef.Arguments[0].Value, gamma, env, paramEnv);
            if (_types.TryGetDictionaryInfo(recv.Type, out _, out var dictId, out var kType, out var vType))
            {
                var kConv = _translator.ConvertTo(arg, kType!);
                AddObligation("CollectionBounds", $"Dictionary key exists for indexer '{propRef.Syntax}'", propRef.Syntax.GetLocation(), gamma,
                    $"(select (map_has_{dictId} {recv.Term}) {kConv.Term})");
                var valTerm = new SmtTypedTerm($"(select (map_data_{dictId} {recv.Term}) {kConv.Term})", vType!);
                EmitOnDemandElementAssumptions($"(select (map_has_{dictId} {recv.Term}) {kConv.Term})", valTerm, propRef.Syntax.GetLocation());
                return valTerm;
            }
            if (_types.TryGetSequenceInfo(recv.Type, out _, out var seqId, out var eType))
            {
                var idxTerm = _translator.IntTermForIndex(arg);
                var boundsCond = $"(and (>= {idxTerm} 0) (< {idxTerm} (seq_count_{seqId} {recv.Term})))";
                AddObligation("CollectionBounds", $"Sequence index within bounds for '{propRef.Syntax}'", propRef.Syntax.GetLocation(), gamma, boundsCond);
                var elemVal = new SmtTypedTerm($"(select (seq_elems_{seqId} {recv.Term}) {idxTerm})", eType!);
                EmitOnDemandElementAssumptions(boundsCond, elemVal, propRef.Syntax.GetLocation());
                return elemVal;
            }
        }

        // Static well-known properties
        if (propRef.Instance is null)
        {
            var containing = prop.ContainingType?.OriginalDefinition.ToDisplayString() ?? string.Empty;
            if (containing.StartsWith("System.Collections.Immutable.", StringComparison.Ordinal) && prop.Name == "Empty")
            {
                return new SmtTypedTerm(_types.GetDefaultTerm(prop.Type), prop.Type);
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
            var recv = LowerExpression(propRef.Instance, gamma, env, paramEnv);
            return _translator.TranslateMemberOnReceiver(recv, prop.Name);
        }

        return BindSsaValue("prop", new SmtTypedTerm(_types.GetDefaultTerm(prop.Type), prop.Type), isFreshFreeSymbol: true);
    }

    private SmtTypedTerm LowerFieldReference(IFieldReferenceOperation fieldRef, string gamma, BlockEnv env, IReadOnlyDictionary<string, SmtTypedTerm> paramEnv)
    {
        var field = fieldRef.Field;
        if (fieldRef.Instance is null)
        {
            var containing = field.ContainingType?.OriginalDefinition.ToDisplayString() ?? string.Empty;
            if (containing.StartsWith("System.Collections.Immutable.", StringComparison.Ordinal) && field.Name == "Empty")
            {
                return new SmtTypedTerm(_types.GetDefaultTerm(field.Type), field.Type);
            }
            if (field.ContainingType?.ToDisplayString() == "System.Guid" && field.Name == "Empty")
            {
                return new SmtTypedTerm("0", field.Type);
            }
            if (field.HasConstantValue && field.ConstantValue is not null)
            {
                if (field.ConstantValue is int iv) return new SmtTypedTerm(_translator.FormatIntLiteral(iv, 32), field.Type);
                if (field.ConstantValue is long lv) return new SmtTypedTerm(_translator.FormatLongLiteral(lv), field.Type);
                if (field.ConstantValue is decimal dv) return new SmtTypedTerm(ContractExpressionTranslator.FormatDecimalLiteral(dv), field.Type);
                if (field.Type.TypeKind == TypeKind.Enum)
                {
                    var num = Convert.ToInt64(field.ConstantValue, CultureInfo.InvariantCulture);
                    return new SmtTypedTerm(num < 0 ? $"(- {-num})" : num.ToString(CultureInfo.InvariantCulture), field.Type);
                }
            }
            if (field.ContainingType?.OriginalDefinition.ToDisplayString() == "Verifiable.Contracts.Unit" && field.Name == "Value")
            {
                return new SmtTypedTerm(_types.GetDefaultTerm(field.Type), field.Type);
            }
            return BindSsaValue("fld", new SmtTypedTerm(_types.GetDefaultTerm(field.Type), field.Type), isFreshFreeSymbol: true);
        }

        var recv = LowerExpression(fieldRef.Instance, gamma, env, paramEnv);
        return _translator.TranslateMemberOnReceiver(recv, field.Name);
    }

    private SmtTypedTerm LowerWithOperation(IWithOperation withOp, string gamma, BlockEnv env, IReadOnlyDictionary<string, SmtTypedTerm> paramEnv)
    {
        var baseVal = LowerExpression(withOp.Operand, gamma, env, paramEnv);
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
                    var val = LowerExpression(sa.Value, gamma, env, paramEnv);
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

    private SmtTypedTerm LowerObjectCreation(IObjectCreationOperation objCreate, string gamma, BlockEnv env, IReadOnlyDictionary<string, SmtTypedTerm> paramEnv)
    {
        var targetType = objCreate.Type!;
        if (!_types.TryGetComposite(targetType, out var comp))
        {
            return new SmtTypedTerm(_types.GetDefaultTerm(targetType), targetType);
        }

        var argMap = new Dictionary<string, SmtTypedTerm>(StringComparer.OrdinalIgnoreCase);
        foreach (var arg in objCreate.Arguments)
        {
            if (arg.Parameter is not null)
            {
                var v = LowerExpression(arg.Value, gamma, env, paramEnv);
                argMap[arg.Parameter.Name] = _translator.ConvertTo(v, arg.Parameter.Type);
            }
        }

        if (objCreate.Initializer is not null)
        {
            foreach (var init in objCreate.Initializer.Initializers)
            {
                if (init is ISimpleAssignmentOperation sa && sa.Target is IMemberReferenceOperation mr)
                {
                    var v = LowerExpression(sa.Value, gamma, env, paramEnv);
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

    private SmtTypedTerm LowerTuple(ITupleOperation tuple, string gamma, BlockEnv env, IReadOnlyDictionary<string, SmtTypedTerm> paramEnv)
    {
        var tupleType = tuple.Type!;
        if (!_types.TryGetComposite(tupleType, out var comp) || comp!.Fields.Count == 0)
        {
            return new SmtTypedTerm(_types.GetDefaultTerm(tupleType), tupleType);
        }

        var elems = new List<string>(tuple.Elements.Length);
        for (var i = 0; i < tuple.Elements.Length && i < comp.Fields.Count; i++)
        {
            var e = LowerExpression(tuple.Elements[i], gamma, env, paramEnv);
            elems.Add(_translator.ConvertTo(e, comp.Fields[i].Type).Term);
        }

        return BindSsaValue("tup", new SmtTypedTerm($"({comp.ConstructorName} {string.Join(" ", elems)})", tupleType));
    }

    private SmtTypedTerm LowerCollectionExpression(ICollectionExpressionOperation colExpr, string gamma, BlockEnv env, IReadOnlyDictionary<string, SmtTypedTerm> paramEnv)
    {
        var t = colExpr.Type!;
        if (_types.TryGetSequenceInfo(t, out var seqSort, out var seqId, out var elemType))
        {
            var cur = new SmtTypedTerm(_types.GetDefaultTerm(t), t);
            var i = 0;
            foreach (var elemOp in colExpr.Elements)
            {
                var ev = _translator.ConvertTo(LowerExpression(elemOp, gamma, env, paramEnv), elemType!);
                cur = new SmtTypedTerm(
                    $"(mk-{seqSort} (store (seq_elems_{seqId} {cur.Term}) {i} {ev.Term}) {i + 1})",
                    t);
                i++;
            }
            return BindSsaValue("seq", cur);
        }

        return new SmtTypedTerm(_types.GetDefaultTerm(t), t);
    }

    private SmtTypedTerm LowerInvocation(IInvocationOperation inv, string gamma, BlockEnv env, IReadOnlyDictionary<string, SmtTypedTerm> paramEnv)
    {
        var method = inv.TargetMethod;
        var containing = method.ContainingType;
        var loc = inv.Syntax.GetLocation();

        var argValues = new List<SmtTypedTerm>(inv.Arguments.Length);
        foreach (var arg in inv.Arguments)
        {
            var v = LowerExpression(arg.Value, gamma, env, paramEnv);
            argValues.Add(_translator.ConvertTo(v, arg.Parameter?.Type ?? v.Type));
        }

        var recvTerm = inv.Instance is not null ? LowerExpression(inv.Instance, gamma, env, paramEnv) : null;

        // 1. Refined<T>.From(value) and IsValid(value)
        if (containing is not null && SmtTypeSystem.TryGetRefinedUnderlying(containing, out var underlying, out var pred))
        {
            if (method.Name == "From" && argValues.Count == 1 && _types.TryGetComposite(containing, out var refComp))
            {
                var arg = _translator.ConvertTo(argValues[0], underlying!);
                CheckMethodRequires(method, recvTerm, new[] { arg }, gamma, loc);
                return BindSsaValue("ref_" + SmtTypeSystem.SanitizeName(containing.Name),
                    new SmtTypedTerm($"({refComp!.ConstructorName} {arg.Term})", containing));
            }
            if (method.Name == "IsValid" && argValues.Count == 1 && pred is not null)
            {
                var valEnv = new Dictionary<string, SmtTypedTerm>(StringComparer.Ordinal)
                {
                    ["Value"] = argValues[0],
                    ["value"] = argValues[0]
                };
                try
                {
                    var normalized = pred.Replace("Length", "Value.Length"); // safe simple check if already normalized
                    return _translator.TranslateContractString(pred, valEnv, _compilation.GetSpecialType(SpecialType.System_Boolean));
                }
                catch
                {
                    // Fallback to symbolic boolean
                }
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
                AddObligation("CollectionBounds", $"Key not already present before ImmutableDictionary.Add", loc, gamma,
                    $"(not (select (map_has_{dictId} {recvTerm.Term}) {k.Term}))");
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
                AddObligation("CollectionBounds", "Sequence index within bounds for SetItem", loc, gamma,
                    $"(and (>= {idxTerm} 0) (< {idxTerm} (seq_count_{seqId} {recvTerm.Term})))");
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
            return new SmtTypedTerm(_types.GetDefaultTerm(method.ReturnType), method.ReturnType);
        }

        // 7. Math / decimal / string helpers
        if (containing?.SpecialType is SpecialType.System_Decimal || containing?.ToDisplayString() == "System.Math")
        {
            if (method.Name == "Abs" && argValues.Count == 1)
            {
                var a = argValues[0];
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

        if (containing?.SpecialType == SpecialType.System_String && method.Name == "IsNullOrEmpty" && argValues.Count == 1)
        {
            return new SmtTypedTerm(
                $"(or (= {argValues[0].Term} str-null) (= (str.len (str-text {argValues[0].Term})) 0))",
                _compilation.GetSpecialType(SpecialType.System_Boolean));
        }

        if (method.Name == "Equals" && recvTerm is not null && argValues.Count == 1)
        {
            var (l, r, _) = _translator.UnifyForComparison(recvTerm, argValues[0]);
            return new SmtTypedTerm($"(= {l} {r})", _compilation.GetSpecialType(SpecialType.System_Boolean));
        }

        // 8. General [Verifiable] / [PureAxiom] modular call: check callee [Requires], assume callee [Ensures] + return invariants
        CheckMethodRequires(method, recvTerm, argValues, gamma, loc);

        if (method.ReturnsVoid)
        {
            return new SmtTypedTerm("0", _compilation.GetSpecialType(SpecialType.System_Int32));
        }

        var retTerm = BindSsaValue("call_" + SmtTypeSystem.SanitizeName(method.Name),
            new SmtTypedTerm(_types.GetDefaultTerm(method.ReturnType), method.ReturnType),
            isFreshFreeSymbol: true);

        EmitTypeAssumptions(retTerm, loc);

        var calleeEnsures = GetAttributeStrings(method, VerifiableSymbolHelpers.EnsuresAttr);
        if (calleeEnsures.Count > 0)
        {
            var calleeEnv = new Dictionary<string, SmtTypedTerm>(StringComparer.Ordinal) { ["result"] = retTerm };
            if (recvTerm is not null) calleeEnv["this"] = recvTerm;
            for (var i = 0; i < method.Parameters.Length && i < argValues.Count; i++)
            {
                calleeEnv[method.Parameters[i].Name] = argValues[i];
            }

            var calleeTranslator = new ContractExpressionTranslator(
                _compilation,
                _types,
                method.ContainingType,
                method.Locations.FirstOrDefault(l => l.IsInSource)?.SourceTree);

            foreach (var ens in calleeEnsures)
            {
                try
                {
                    var ensTerm = calleeTranslator.TranslateContractString(ens, calleeEnv, _compilation.GetSpecialType(SpecialType.System_Boolean));
                    _assertions.Add($"(assert {ensTerm.Term})");
                }
                catch (ContractLoweringException ex)
                {
                    _errors.Add((ens, loc, ex.Message));
                }
            }
        }

        return retTerm;
    }

    private void CheckMethodRequires(
        IMethodSymbol callee,
        SmtTypedTerm? recvTerm,
        IReadOnlyList<SmtTypedTerm> args,
        string gamma,
        Location callLoc)
    {
        var reqs = GetAttributeStrings(callee, VerifiableSymbolHelpers.RequiresAttr);
        if (reqs.Count == 0) return;

        var calleeEnv = new Dictionary<string, SmtTypedTerm>(StringComparer.Ordinal);
        if (recvTerm is not null) calleeEnv["this"] = recvTerm;
        for (var i = 0; i < callee.Parameters.Length && i < args.Count; i++)
        {
            calleeEnv[callee.Parameters[i].Name] = args[i];
        }

        var calleeTranslator = new ContractExpressionTranslator(
            _compilation,
            _types,
            callee.ContainingType,
            callee.Locations.FirstOrDefault(l => l.IsInSource)?.SourceTree);

        foreach (var req in reqs)
        {
            try
            {
                var reqTerm = calleeTranslator.TranslateContractString(req, calleeEnv, _compilation.GetSpecialType(SpecialType.System_Boolean));
                AddObligation("Precondition", $"[Requires(\"{req}\")] on {callee.ContainingType?.Name}.{callee.Name}", callLoc, gamma, reqTerm.Term);
            }
            catch (ContractLoweringException ex)
            {
                _errors.Add((req, callLoc, ex.Message));
            }
        }
    }

    private void EmitTypeAssumptions(SmtTypedTerm value, Location loc)
    {
        if (_types.TryGetDictionaryInfo(value.Type, out _, out var dictId, out var kType, out _))
        {
            var kSort = _types.GetSort(kType!);
            _assertions.Add($"(assert (>= (map_count_{dictId} {value.Term}) 0))");
            _assertions.Add($"(assert (= (= (map_count_{dictId} {value.Term}) 0) (= (map_has_{dictId} {value.Term}) ((as const (Array {kSort} Bool)) false))))");
        }
        else if (_types.TryGetSequenceInfo(value.Type, out _, out var seqId, out _))
        {
            _assertions.Add($"(assert (>= (seq_count_{seqId} {value.Term}) 0))");
        }
        else if (_types.TryGetSetInfo(value.Type, out _, out var setId, out var setElem))
        {
            var eSort = _types.GetSort(setElem!);
            _assertions.Add($"(assert (>= (set_count_{setId} {value.Term}) 0))");
            _assertions.Add($"(assert (= (= (set_count_{setId} {value.Term}) 0) (= (set_has_{setId} {value.Term}) ((as const (Array {eSort} Bool)) false))))");
        }

        // Guard payload assumptions for Result<T, E> by IsSuccess
        if (value.Type is INamedTypeSymbol resNamed &&
            resNamed.OriginalDefinition.ToDisplayString() == "Verifiable.Contracts.Result<T, TError>" &&
            _types.TryGetComposite(resNamed, out var resComp))
        {
            var isSuccess = $"({resComp!.Fields.First(f => f.Name == "IsSuccess").AccessorName} {value.Term})";
            var valField = resComp.Fields.First(f => f.Name == "Value");
            EmitOnDemandElementAssumptions(isSuccess, new SmtTypedTerm($"({valField.AccessorName} {value.Term})", valField.Type), loc);
            return;
        }

        foreach (var inv in SmtTypeSystem.GetInvariants(value.Type))
        {
            try
            {
                var tEnv = new Dictionary<string, SmtTypedTerm>(StringComparer.Ordinal) { ["this"] = value };
                var invTerm = _translator.TranslateContractString(inv, tEnv, _compilation.GetSpecialType(SpecialType.System_Boolean));
                _assertions.Add($"(assert {invTerm.Term})");
            }
            catch (ContractLoweringException ex)
            {
                _errors.Add((inv, loc, ex.Message));
            }
        }

        if (_types.TryGetComposite(value.Type, out var comp) &&
            !SmtTypeSystem.TryGetRefinedUnderlying(value.Type, out _, out _))
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

        foreach (var inv in SmtTypeSystem.GetInvariants(element.Type))
        {
            try
            {
                var tEnv = new Dictionary<string, SmtTypedTerm>(StringComparer.Ordinal) { ["this"] = element };
                var invTerm = _translator.TranslateContractString(inv, tEnv, _compilation.GetSpecialType(SpecialType.System_Boolean));
                _assertions.Add($"(assert (=> {guardCond} {invTerm.Term}))");
            }
            catch (ContractLoweringException ex)
            {
                _errors.Add((inv, loc, ex.Message));
            }
        }

        if (_types.TryGetComposite(element.Type, out var comp) &&
            !SmtTypeSystem.TryGetRefinedUnderlying(element.Type, out _, out _))
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

        var mergedEnv = new BlockEnv();
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
                    // Member assignment on a FlowCaptureReference is a 'with' clone; do not mutate the captured source variable.
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
            _assertions.Add($"(assert (= {name} {value.Term}))");
        }
        return new SmtTypedTerm(name, value.Type);
    }

    private void AddObligation(string kind, string description, Location location, string gamma, string goalTerm)
    {
        var preamble = string.Join("\n", _types.Declarations.Concat(_decls).Concat(_assertions));
        _obligations.Add(new ProofObligation(kind, description, location, gamma, goalTerm, preamble, _trackedParams.ToList()));
    }

    private static Dictionary<string, SmtTypedTerm> BuildTranslatorEnv(BlockEnv env, IReadOnlyDictionary<string, SmtTypedTerm> paramEnv)
    {
        var dict = new Dictionary<string, SmtTypedTerm>(StringComparer.Ordinal);
        foreach (var kv in paramEnv)
        {
            dict[kv.Key] = kv.Value;
        }
        foreach (var kv in env.Vars)
        {
            dict[kv.Key.Name] = kv.Value;
        }
        return dict;
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
        return false;
    }

    private static IReadOnlyList<string> GetAttributeStrings(ISymbol symbol, string attrName)
    {
        var list = new List<string>();
        foreach (var attr in symbol.GetAttributes())
        {
            if (attr.AttributeClass?.ToDisplayString() == attrName &&
                attr.ConstructorArguments.Length == 1 &&
                attr.ConstructorArguments[0].Value is string s)
            {
                list.Add(s);
            }
        }
        return list;
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

        // Build reverse edges
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
