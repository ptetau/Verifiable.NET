using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Verifiable.Shared;

namespace Verifiable.Analyzers.Smt;

internal sealed record GoalClauseInfo(
    string ClauseText,
    string ClauseSmtTerm,
    string? Operator = null,
    string? LeftText = null,
    string? LeftSmtTerm = null,
    string? RightText = null,
    string? RightSmtTerm = null);

internal sealed record ProofObligation(
    string Kind,
    string ContractText,
    Location Location,
    string PathCondition,
    string GoalTerm,
    string ScriptPreamble,
    IReadOnlyList<(string ParamName, string SmtName)> TrackedParameters,
    string? Reason = null,
    Location? ContractLocation = null,
    string? ContractLocationLabel = null,
    string? ResultSmtTerm = null,
    IReadOnlyList<GoalClauseInfo>? GoalClauses = null);

internal sealed record NamedPremise(
    string Id,
    string Kind,
    string Label,
    string? Reason,
    string? FilePath,
    int Line,
    string SmtFormula);

/// <summary>
/// A callee guarantee assumed at a call, kept so the analyzer can check that it can hold there.
/// </summary>
/// <param name="Label">The callee and the guarantee as written, e.g. <c>Ax.Weird: Spec.Ensures&lt;int&gt;(r =&gt; r &gt; x)</c>.</param>
/// <param name="Reason">The guarantee's <c>because</c> text, if any.</param>
/// <param name="CallLocation">The call that assumes the guarantee.</param>
/// <param name="GuaranteeLocation">Where the guarantee is declared.</param>
/// <param name="Context">What is known at the call: the path to it, the guarantee's own guard and the callee's preconditions.</param>
/// <param name="Guarantee">The guarantee. It is assumed under the path and guard only, not the preconditions.</param>
/// <param name="AssertionsBefore">How many assertions were made before this one.</param>
/// <param name="ObligationsBefore">How many goals were emitted before this one; only later goals can rely on it.</param>
internal sealed record CalleeGuarantee(
    string Label,
    string? Reason,
    Location CallLocation,
    Location GuaranteeLocation,
    string Context,
    string Guarantee,
    int AssertionsBefore,
    int ObligationsBefore);

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
    IReadOnlyList<NamedPremise> Premises,
    IReadOnlyList<GoalClauseInfo>? GoalClauses = null);

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
    private readonly bool _nonDefaultImmutableArrays;
    private readonly SmtTypeSystem _types;
    private readonly ContractExpressionTranslator _translator;

    private readonly List<string> _decls = new();
    private readonly Dictionary<IFieldSymbol, SmtTypedTerm> _staticReadonlyFields = new(SymbolEqualityComparer.Default);
    private readonly List<string> _assertions = new();
    private readonly List<ProofObligation> _obligations = new();
    private readonly List<NamedPremise> _premises = new();
    private readonly List<CalleeGuarantee> _calleeGuarantees = new();
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

    private sealed record RequiresSpec(
        IOperation Condition,
        Location Location,
        string DisplayText,
        string? Because = null,
        IMethodSymbol? StubMethod = null,
        IOperation? GuardCondition = null);

    private sealed record EnsuresSpec(
        IParameterSymbol? LambdaParam,
        IOperation BodyExpr,
        Location Location,
        string DisplayText,
        string? Because = null,
        IMethodSymbol? StubMethod = null,
        IOperation? GuardCondition = null);

    private sealed record LoopSpecGroup(
        ILoopOperation Loop,
        List<(IOperation Condition, Location Location, string DisplayText, string? Because)> LoopInvariants,
        List<(IOperation Measure, Location Location, string DisplayText, string? Because)> Decreases);

    private sealed class MethodSpecs
    {
        public HashSet<SyntaxNode> AnnotationCalls { get; } = new();
        public List<RequiresSpec> Requires { get; } = new();
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

    public CfgSsaLowerer(
        Compilation compilation,
        IMethodSymbol method,
        bool trackProofPremises = false,
        bool closedEnums = false,
        bool nonDefaultImmutableArrays = false)
    {
        _compilation = compilation;
        _method = method;
        _trackProofPremises = trackProofPremises;
        _closedEnums = closedEnums;
        _nonDefaultImmutableArrays = nonDefaultImmutableArrays;
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
            _obligations.Where(ob => ob.Kind is "Precondition" or "Overflow" or "DivisionByZero" or "CollectionBounds").ToList(), _errors.ToList());
    }

    private Microsoft.CodeAnalysis.Text.TextSpan? _nonNullTargetSpan;

    public LoweringOutcome LowerNonNullChecks(
        ControlFlowGraph cfg,
        ImmutableArray<IOperation> rawOperationBlocks,
        Microsoft.CodeAnalysis.Text.TextSpan targetSpan)
    {
        _nonNullTargetSpan = targetSpan;
        try
        {
            Lower(cfg, rawOperationBlocks);
            var checks = _obligations.Where(ob => ob.Kind == "NonNullCheck").ToList();
            return new LoweringOutcome(checks, _errors.ToList());
        }
        finally
        {
            _nonNullTargetSpan = null;
        }
    }

    /// <summary>Named premises recorded so far (only when constructed with <c>trackProofPremises: true</c>).</summary>
    internal IReadOnlyList<NamedPremise> Premises => _premises;

    /// <summary>Callee guarantees assumed so far, in the order they were asserted.</summary>
    internal IReadOnlyList<CalleeGuarantee> CalleeGuarantees => _calleeGuarantees;

    /// <summary>Goals emitted so far.</summary>
    internal int ObligationCount => _obligations.Count;

    /// <summary>All declarations, then the first <paramref name="assertionCount"/> assertions.</summary>
    internal string PremisePrefix(int assertionCount) =>
        string.Join("\n", _types.Declarations.Concat(_decls).Concat(_assertions.Take(assertionCount)));

    /// <summary>Test seam: when set on the current async flow, lowering throws an unexpected exception.</summary>
    internal static readonly System.Threading.AsyncLocal<bool> SimulateInternalFailure = new();

    public LoweringOutcome Lower(ControlFlowGraph cfg, ImmutableArray<IOperation> rawOperationBlocks)
    {
        if (SimulateInternalFailure.Value)
        {
            throw new InvalidOperationException("simulated lowering bug");
        }

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
            // Not "p_this": a parameter named @this would get that name.
            const string thisSmt = "p_$this";
            _decls.Add($"(declare-const {thisSmt} {sort})");
            _trackedParams.Add(("this", thisSmt));
            var thisTerm = new SmtTypedTerm(thisSmt, recvType);
            _entryThis = thisTerm;
            entryEnv.ThisOverride = thisTerm;
            EmitTypeAssumptions(thisTerm, methodLoc);
        }

        foreach (var p in _method.Parameters)
        {
            var sort = _types.GetSort(p.Type);
            var smtName = "p_" + SmtTypeSystem.SymbolPart(p.Name);
            _decls.Add($"(declare-const {smtName} {sort})");
            _trackedParams.Add((p.Name, smtName));
            var pTerm = new SmtTypedTerm(smtName, p.Type);
            entryEnv.Vars[p] = pTerm;
            _entryParamVars[p] = pTerm;
            var pLoc = p.Locations.FirstOrDefault() ?? methodLoc;
            EmitTypeAssumptions(pTerm, pLoc);
            if (!_nonDefaultImmutableArrays &&
                VerifiableSymbolHelpers.IsNonDefaultParameter(p, globalNonDefault: false) &&
                _types.TryGetSequenceInfo(p.Type, out _, out var pSeqId, out _, out var pIsImmArr) &&
                pIsImmArr)
            {
                AddAssertion($"(not (seq_is_default_{pSeqId} {pTerm.Term}))", "NonDefault", $"[NonDefault] parameter '{p.Name}'", pLoc);
            }
        }

        _currentSpecs = ExtractMethodSpecs(_compilation, _method, rawOperationBlocks);
        foreach (var err in _currentSpecs.Errors)
        {
            _errors.Add(err);
        }

        // Method Spec.Requires / Spec.RequiresWhen entry assumptions
        foreach (var req in _currentSpecs.Requires)
        {
            try
            {
                if (req.GuardCondition is not null)
                {
                    var guardTerm = LowerExpression(req.GuardCondition, "true", entryEnv, emitObligations: false);
                    var reqTerm = LowerExpression(req.Condition, guardTerm.Term, entryEnv, emitObligations: false);
                    AddAssertion($"(=> {guardTerm.Term} {reqTerm.Term})", "Requires", req.DisplayText, req.Location, req.Because);
                }
                else
                {
                    var reqTerm = LowerExpression(req.Condition, "true", entryEnv, emitObligations: false);
                    AddAssertion(reqTerm.Term, "Requires", req.DisplayText, req.Location, req.Because);
                }
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
            if (synRef.GetSyntax() is MethodDeclarationSyntax md &&
                compilation.ContainsSyntaxTree(md.SyntaxTree))
            {
                var model = compilation.GetSemanticModel(md.SyntaxTree);
                foreach (var inv in LeadingContractBlock.GetLeadingSpecCalls(md, c => VerifiableSymbolHelpers.BindsToSpec(model, c)))
                {
                    if (model.GetSymbolInfo(inv).Symbol is IMethodSymbol target &&
                        (VerifiableSymbolHelpers.IsSpecCall(target, "Requires") ||
                         VerifiableSymbolHelpers.IsSpecCall(target, "RequiresWhen")))
                    {
                        return true;
                    }
                }
            }
        }
        return CrossAssemblyContractCache.HasRequires(compilation, method);
    }

    /// <summary>True if this <c>Spec.Requires</c> / <c>Spec.RequiresWhen</c> is in its member's leading contract block.</summary>
    private static bool IsPreconditionCall(Compilation compilation, IInvocationOperation inv)
    {
        var model = inv.SemanticModel;
        if (model is null || model.SyntaxTree != inv.Syntax.SyntaxTree)
        {
            if (!compilation.ContainsSyntaxTree(inv.Syntax.SyntaxTree))
            {
                return false;
            }
            model = compilation.GetSemanticModel(inv.Syntax.SyntaxTree);
        }
        return VerifiableSymbolHelpers.IsPrecondition(model, inv.Syntax);
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

        if (roots.Count == 0 &&
            existingBlocks.IsDefaultOrEmpty &&
            CrossAssemblyContractCache.TryGetMethodSpecs(compilation, method, out var extReqs, out var extEns))
        {
            foreach (var req in extReqs)
            {
                specs.Requires.Add(new RequiresSpec(
                    req.Expression,
                    Location.None,
                    $"Spec.Requires({req.RawExpression})",
                    req.Description,
                    req.StubMethod));
            }

            foreach (var ens in extEns)
            {
                var resultParam = ens.StubMethod.Parameters.LastOrDefault(p => p.Name == CrossAssemblyContractCache.ResultPlaceholder);
                specs.Ensures.Add(new EnsuresSpec(
                    resultParam,
                    ens.Expression,
                    Location.None,
                    $"Spec.Ensures({ens.RawExpression})",
                    ens.Description,
                    ens.StubMethod));
            }

            return specs;
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
                if (inv.TargetMethod.Name is "Ensures" or "EnsuresWhen" or "Requires" or "RequiresWhen" or "LoopInvariant" or "Decreases")
                    specs.AnnotationCalls.Add(inv.Syntax);

                var isWhen = inv.TargetMethod.Name is "RequiresWhen" or "EnsuresWhen";
                var becauseIdx = isWhen ? 2 : 1;
                var because = inv.Arguments.Length > becauseIdx
                    ? ExtractConstantStringArg(GetOrderedArgOp(inv, becauseIdx))
                    : null;

                // A Requires outside the leading contract block is not a precondition. VerifiableSmtAnalyzer reports it
                // as VER101; here it is dropped, so it is neither assumed at entry nor required of callers.
                if (inv.TargetMethod.Name is "Requires" or "RequiresWhen" &&
                    !IsPreconditionCall(compilation, inv))
                {
                    return;
                }

                switch (inv.TargetMethod.Name)
                {
                    case "Requires" when inv.Arguments.Length is 1 or 2:
                        specs.Requires.Add(new RequiresSpec(GetOrderedArgOp(inv, 0), loc, text, because));
                        return;

                    case "RequiresWhen" when inv.Arguments.Length is 2 or 3:
                        specs.Requires.Add(new RequiresSpec(GetOrderedArgOp(inv, 1), loc, text, because, GuardCondition: GetOrderedArgOp(inv, 0)));
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

                        if (!TryExtractLambdaPredicate(GetOrderedArgOp(inv, 0), out var lambdaParam, out var bodyExpr, out var lambdaError))
                        {
                            specs.Errors.Add((text, loc, lambdaError!));
                            return;
                        }

                        specs.Ensures.Add(new EnsuresSpec(lambdaParam, bodyExpr!, loc, text, because));
                        return;
                    }

                    case "Ensures" when inv.TargetMethod.TypeArguments.Length == 0 && inv.Arguments.Length is 1 or 2:
                        specs.Ensures.Add(new EnsuresSpec(null, GetOrderedArgOp(inv, 0), loc, text, because));
                        return;

                    case "EnsuresWhen" when inv.TargetMethod.TypeArguments.Length == 1 && inv.Arguments.Length is 2 or 3:
                    {
                        var declaredType = inv.TargetMethod.TypeArguments[0];
                        if (!SymbolEqualityComparer.Default.Equals(declaredType, method.ReturnType))
                        {
                            specs.Errors.Add((text, loc,
                                $"Spec.EnsuresWhen<{declaredType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}> type argument does not match method return type '{method.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}'."));
                            return;
                        }

                        if (!TryExtractLambdaPredicate(GetOrderedArgOp(inv, 1), out var lambdaParam, out var bodyExpr, out var lambdaError))
                        {
                            specs.Errors.Add((text, loc, lambdaError!));
                            return;
                        }

                        specs.Ensures.Add(new EnsuresSpec(lambdaParam, bodyExpr!, loc, text, because, GuardCondition: GetOrderedArgOp(inv, 0)));
                        return;
                    }

                    case "EnsuresWhen" when inv.TargetMethod.TypeArguments.Length == 0 && inv.Arguments.Length is 2 or 3:
                        specs.Ensures.Add(new EnsuresSpec(null, GetOrderedArgOp(inv, 1), loc, text, because, GuardCondition: GetOrderedArgOp(inv, 0)));
                        return;

                    case "LoopInvariant" when inv.Arguments.Length is 1 or 2:
                        if (enclosingLoop is null)
                        {
                            specs.Errors.Add((text, loc, "Spec.LoopInvariant(...) must be called inside a loop body."));
                            return;
                        }
                        loopGroups[enclosingLoop].LoopInvariants.Add((GetOrderedArgOp(inv, 0), loc, text, because));
                        return;

                    case "Decreases" when inv.Arguments.Length is 1 or 2:
                        if (enclosingLoop is null)
                        {
                            specs.Errors.Add((text, loc, "Spec.Decreases(...) must be called inside a loop body."));
                            return;
                        }
                        loopGroups[enclosingLoop].Decreases.Add((GetOrderedArgOp(inv, 0), loc, text, because));
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
            var smtVar = BindSsaValue("loop_" + SmtTypeSystem.SymbolPart(sym.Name), new SmtTypedTerm(_types.GetDefaultTerm(type), type), isFreshFreeSymbol: true);
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
                // No lowerer name starts with "assume_", so this label cannot clash with a parameter or local.
                var assumeId = $"assume_result_{++_premiseCounter}";
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
            var queryClauses = ExtractGoalClauses(_queryAssertOp!, goalGamma, queryEnv);
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
                _premises.ToList(),
                queryClauses));
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

                var effectiveGamma = gamma;
                if (ens.GuardCondition is not null)
                {
                    var guardTerm = LowerExpression(ens.GuardCondition, gamma, ensEnv, emitObligations: false);
                    effectiveGamma = CombineAnd(gamma, guardTerm.Term);
                }

                var goal = LowerExpression(ens.BodyExpr, effectiveGamma, ensEnv, emitObligations: false);
                var clauses = ExtractGoalClauses(ens.BodyExpr, effectiveGamma, ensEnv);
                AddObligation(
                    "Postcondition",
                    ens.DisplayText,
                    retLoc,
                    effectiveGamma,
                    goal.Term,
                    ens.Because,
                    contractLocation: ens.Location,
                    contractLocationLabel: "The postcondition that failed is declared here:",
                    resultSmtTerm: retTerm.Term,
                    goalClauses: clauses);
            }
            catch (ContractLoweringException ex)
            {
                _errors.Add((ens.DisplayText, ens.Location, ex.Message));
            }
        }

        if (VerifiableSymbolHelpers.IsNonDefaultReturn(_method, _nonDefaultImmutableArrays) &&
            _types.TryGetSequenceInfo(retTerm.Type, out _, out var retSeqId, out _, out var retIsImmArr) &&
            retIsImmArr)
        {
            AddObligation(
                "Postcondition",
                $"[NonDefault] return of '{_method.Name}'",
                retLoc,
                gamma,
                $"(not (seq_is_default_{retSeqId} {retTerm.Term}))",
                "Return value must be an initialized ImmutableArray (not default).",
                resultSmtTerm: retTerm.Term);
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
            var effectiveGamma = CombineAnd(gamma, successCond);
            if (_nonDefaultImmutableArrays &&
                _types.TryGetSequenceInfo(valTerm.Type, out _, out var resSeqId, out _, out var resIsImmArr) &&
                resIsImmArr)
            {
                AddObligation(
                    "TypeInvariant",
                    $"[NonDefault] Result payload of '{_method.Name}'",
                    retLoc,
                    effectiveGamma,
                    $"(not (seq_is_default_{resSeqId} {valTerm.Term}))",
                    "Success payload ImmutableArray must be initialized (not default).");
            }
            EmitValueInvariantObligations(valTerm, effectiveGamma, retLoc);
            return;
        }

        EmitValueInvariantObligations(retTerm, gamma, retLoc);
    }

    private void EmitValueInvariantObligations(SmtTypedTerm value, string gamma, Location loc)
    {
        if (TryEmitCompoundValidity(value, gamma, loc)) return;

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
                var clauses = ExtractGoalClauses(isValidBody!, gamma, valEnv);
                AddValidityObligation(
                    "TypeInvariant",
                    $"{value.Type.Name}.IsValid",
                    loc,
                    gamma,
                    goal.Term,
                    typeDescription,
                    contractLocation: isValidMethod!.Locations.FirstOrDefault(l => l.IsInSource),
                    contractLocationLabel: $"The `{value.Type.Name}.IsValid` predicate is declared here:",
                    resultSmtTerm: unwrapped.Term,
                    goalClauses: clauses);
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
                var clauses = ExtractGoalClauses(invBody!, gamma, invEnv);
                AddValidityObligation(
                    "TypeInvariant",
                    $"[Invariant] {value.Type.Name}.{invMember.Name}",
                    loc,
                    gamma,
                    goal.Term,
                    invDescription,
                    contractLocation: invMember.Locations.FirstOrDefault(l => l.IsInSource),
                    contractLocationLabel: $"The `[Invariant]` member `{value.Type.Name}.{invMember.Name}` is declared here:",
                    resultSmtTerm: value.Term,
                    goalClauses: clauses);
            }
            catch (ContractLoweringException ex)
            {
                _errors.Add(($"{value.Type.Name}.{invMember.Name}", loc, ex.Message));
            }
        }

        if (IsClosedEnum(value.Type, out var enumType, out var enumVals))
        {
            var domainFormula = BuildClosedEnumFormula(value.Term, enumVals);
            AddValidityObligation("TypeInvariant", $"Closed enum domain for '{enumType!.Name}'", loc, gamma, domainFormula,
                $"Values of closed enum '{enumType.Name}' must be one of its declared members.");
            return;
        }

        if (_types.TryGetNullableInfo(value.Type, out _, out var optId, out var optInner) &&
            IsClosedEnum(optInner!, out var optEnumType, out var optEnumVals))
        {
            var domainFormula = BuildClosedEnumFormula($"(opt-val-{optId} {value.Term})", optEnumVals);
            AddValidityObligation("TypeInvariant", $"Closed enum domain for '{optEnumType!.Name}'", loc,
                CombineAnd(gamma, $"(not (= {value.Term} opt-none-{optId}))"),
                domainFormula,
                $"Values of closed enum '{optEnumType.Name}' must be one of its declared members.");
            return;
        }

        if (_types.TryGetComposite(value.Type, out var comp) && !SmtTypeSystem.TryGetConstrainedUnderlying(value.Type, out _))
        {
            foreach (var f in comp!.Fields)
            {
                if ((f.IsNonDefault || _nonDefaultImmutableArrays) &&
                    _types.TryGetSequenceInfo(f.Type, out _, out var fSeqId, out _, out var fIsImmArr) &&
                    fIsImmArr)
                {
                    AddValidityObligation(
                        "TypeInvariant",
                        $"[NonDefault] field '{value.Type.Name}.{f.Name}'",
                        loc,
                        gamma,
                        $"(not (seq_is_default_{fSeqId} ({f.AccessorName} {value.Term})))",
                        $"Field '{f.Name}' on '{value.Type.Name}' must be an initialized ImmutableArray (not default).");
                }

                if (SmtTypeSystem.HasAnyInvariants(f.Type) ||
                    HasAnyClosedEnum(f.Type) || HasValidityRule(f.Type) ||
                    SmtTypeSystem.HasAnyNonDefaultImmutableArray(f.Type, _nonDefaultImmutableArrays))
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
        var result = LowerExpressionCore(op, gamma, env, emitObligations, inlineDepth);

        if (emitObligations &&
            _nonNullTargetSpan is { } targetSpan &&
            !op.IsImplicit &&
            op.Syntax.Span == targetSpan)
        {
            if (result.Type.SpecialType == SpecialType.System_String)
            {
                AddObligation(
                    "NonNullCheck",
                    $"{op.Syntax} != null",
                    op.Syntax.GetLocation(),
                    gamma,
                    $"(not (= {result.Term} str-null))");
            }
            else if (_types.TryGetNullableInfo(result.Type, out _, out var optId, out _))
            {
                AddObligation(
                    "NonNullCheck",
                    $"{op.Syntax} != null",
                    op.Syntax.GetLocation(),
                    gamma,
                    $"(not (= {result.Term} opt-none-{optId}))");
            }
        }

        return result;
    }

    private SmtTypedTerm LowerExpressionCore(
        IOperation op,
        string gamma,
        BlockEnv env,
        bool emitObligations,
        int inlineDepth)
    {
        switch (op)
        {
            case IConversionOperation conv:
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
                if (conv.Type is { } target)
                {
                    var convChecked = IsConversionChecked(conv, _compilation);
                    if (!SymbolEqualityComparer.Default.Equals(inner.Type, target) &&
                        _types.TryGetNullableInfo(inner.Type, out _, out var srcId, out var srcType) &&
                        _types.TryGetNullableInfo(target, out _, out var dstId, out var dstType))
                    {
                        var payload = new SmtTypedTerm($"(opt-val-{srcId} {inner.Term})", srcType!);
                        var nonNullGamma = CombineAnd(gamma, $"(not (= {inner.Term} opt-none-{srcId}))");
                        SmtTypedTerm converted;
                        if (TryLowerIntegralOrEnumConversion(payload, dstType!, convChecked, nonNullGamma, conv.Syntax.GetLocation(), out var convPayload))
                        {
                            converted = convPayload;
                        }
                        else if (inCoalesce && conv.IsImplicit)
                        {
                            converted = ConvertNullableValue(payload, dstType!);
                        }
                        else
                        {
                            converted = _translator.ConvertTo(payload, dstType!);
                        }
                        return new SmtTypedTerm($"(ite (= {inner.Term} opt-none-{srcId}) opt-none-{dstId} (opt-some-{dstId} {converted.Term}))", target);
                    }

                    if (!SymbolEqualityComparer.Default.Equals(inner.Type, target) &&
                        !_types.TryGetNullableInfo(inner.Type, out _, out _, out _) &&
                        _types.TryGetNullableInfo(target, out _, out var targetOptId, out var targetInner) &&
                        TryLowerIntegralOrEnumConversion(inner, targetInner!, convChecked, gamma, conv.Syntax.GetLocation(), out var innerConverted))
                    {
                        return new SmtTypedTerm($"(opt-some-{targetOptId} {innerConverted.Term})", target);
                    }

                    if (!SymbolEqualityComparer.Default.Equals(inner.Type, target) &&
                        !_types.TryGetNullableInfo(inner.Type, out _, out _, out _) &&
                        !_types.TryGetNullableInfo(target, out _, out _, out _) &&
                        TryLowerIntegralOrEnumConversion(inner, target, convChecked, gamma, conv.Syntax.GetLocation(), out var directConverted))
                    {
                        return directConverted;
                    }

                    if (!SymbolEqualityComparer.Default.Equals(inner.Type, target) &&
                        _types.TryGetNullableInfo(inner.Type, out _, out var unwrapSrcId, out var unwrapSrcType) &&
                        !_types.TryGetNullableInfo(target, out _, out _, out _) &&
                        !conv.Conversion.IsUserDefined)
                    {
                        // `(int)x` on an int?: throws InvalidOperationException when x is empty, else the payload converted.
                        if (!inCoalesce && (emitObligations || _inQuantifierDepth > 0))
                        {
                            EmitSafetyObligation("Precondition", $"Nullable must have a value for the cast in '{conv.Syntax}'",
                                conv.Syntax.GetLocation(), gamma, $"(not (= {inner.Term} opt-none-{unwrapSrcId}))");
                        }
                        var unwrapped = new SmtTypedTerm($"(opt-val-{unwrapSrcId} {inner.Term})", unwrapSrcType!);
                        if (SymbolEqualityComparer.Default.Equals(unwrapSrcType, target))
                            return unwrapped;
                        var unwrapNonNullGamma = CombineAnd(gamma, $"(not (= {inner.Term} opt-none-{unwrapSrcId}))");
                        if (TryLowerIntegralOrEnumConversion(unwrapped, target, convChecked, unwrapNonNullGamma, conv.Syntax.GetLocation(), out var unwrapConverted))
                            return unwrapConverted;
                        return inCoalesce && conv.IsImplicit
                            ? ConvertNullableValue(unwrapped, target)
                            : _translator.ConvertTo(unwrapped, target);
                    }

                    if (inCoalesce && conv.IsImplicit)
                        return ConvertNullableValue(inner, target);
                    return _translator.ConvertTo(inner, target);
                }
                return inner;

            case IParenthesizedOperation paren:
                return LowerExpression(paren.Operand, gamma, env, emitObligations, inlineDepth);

            case ILocalReferenceOperation lr:
                if (env.Vars.TryGetValue(lr.Local, out var lVal)) return lVal;
                return new SmtTypedTerm(_types.GetDefaultTerm(lr.Local.Type), lr.Local.Type);

            case IParameterReferenceOperation pr:
                if (env.Vars.TryGetValue(pr.Parameter, out var pVal)) return pVal;
                if (pr.Parameter.Name == CrossAssemblyContractCache.ThisPlaceholder && env.ThisOverride is not null) return env.ThisOverride;
                if (pr.Parameter.Name == CrossAssemblyContractCache.ResultPlaceholder && env.SpecResult is not null) return env.SpecResult;
                return new SmtTypedTerm("p_" + SmtTypeSystem.SymbolPart(pr.Parameter.Name), pr.Parameter.Type);

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
                if (un.OperatorKind == UnaryOperatorKind.Not)
                    return new SmtTypedTerm($"(not {uOp.Term})", _compilation.GetSpecialType(SpecialType.System_Boolean));
                if (un.OperatorKind == UnaryOperatorKind.Minus)
                {
                    if (CalendarArithmetic.Classify(uOp.Type) == CalendarTypeKind.TimeSpan)
                    {
                        EmitSafetyObligation("Overflow", $"TimeSpan unary negation overflow on TimeSpan.MinValue in '{un.Syntax}'",
                            un.Syntax.GetLocation(), gamma,
                            $"(not (= {uOp.Term} {CalendarArithmetic.FormatLongInt(CalendarArithmetic.TimeSpanMinTicks)}))");
                        return new SmtTypedTerm($"(- {uOp.Term})", uOp.Type);
                    }
                    uOp = _translator.UnwrapRefinedIfApplicable(uOp);
                    var sort = _types.GetSort(uOp.Type);
                    if (_types.UseBitVec && sort is "(_ BitVec 32)" or "(_ BitVec 64)")
                    {
                        if (emitObligations)
                        {
                            var minVal = sort == "(_ BitVec 32)" ? "(_ bv2147483648 32)" : "(_ bv9223372036854775808 64)";
                            EmitSafetyObligation("Overflow", $"Unary negation overflow check on '{un.Syntax}'", un.Syntax.GetLocation(), gamma, $"(not (= {uOp.Term} {minVal}))");
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
                if (bin.OperatorKind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals &&
                    TryLowerUserEquality(bin, lhs, rhs, gamma, emitObligations, inlineDepth, out var userEq))
                    return userEq;
                return LowerBinaryOperator(bin.OperatorKind, lhs, rhs, bin.Type!, gamma, bin.Syntax.GetLocation(), emitObligations);

            case ITupleBinaryOperation tupleBin:
                return LowerTupleBinary(tupleBin, gamma, env, emitObligations, inlineDepth);

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

            case IIsTypeOperation isType:
                var typeSubject = LowerExpression(isType.ValueOperand, gamma, env, emitObligations, inlineDepth);
                var typeTest = LowerTypeTest(typeSubject, isType.TypeOperand, isType);
                return new SmtTypedTerm(isType.IsNegated ? $"(not {typeTest})" : typeTest, _compilation.GetSpecialType(SpecialType.System_Boolean));

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
            (SpecialType.System_Int32, SpecialType.System_Int64 or SpecialType.System_Decimal or SpecialType.System_Double) or
            (SpecialType.System_Int64, SpecialType.System_Decimal or SpecialType.System_Double))
            return _translator.ConvertTo(value, target);
        throw new ContractLoweringException($"Unsupported nullable conversion from '{value.Type}' to '{target}'.");
    }

    private SmtTypedTerm LowerLiftedComparison(IBinaryOperation bin, string gamma, BlockEnv env, bool emitObligations, int inlineDepth)
    {
        var equality = bin.OperatorKind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals;
        if (bin.Type?.SpecialType != SpecialType.System_Boolean ||
            (!equality && bin.OperatorKind is not (BinaryOperatorKind.LessThan or BinaryOperatorKind.LessThanOrEqual or
                BinaryOperatorKind.GreaterThan or BinaryOperatorKind.GreaterThanOrEqual)) ||
            (bin.OperatorMethod is { } method &&
             method.ContainingType.SpecialType != SpecialType.System_Decimal &&
             CalendarArithmetic.Classify(method.ContainingType) == CalendarTypeKind.None))
            throw new ContractLoweringException($"Unsupported lifted operator in '{bin.Syntax}'.");

        var left = LowerExpression(bin.LeftOperand, gamma, env, emitObligations, inlineDepth);
        var right = LowerExpression(bin.RightOperand, gamma, env, emitObligations, inlineDepth);
        if (!_types.TryGetNullableInfo(left.Type, out _, out var leftId, out var inner) ||
            !_types.TryGetNullableInfo(right.Type, out _, out var rightId, out _) || leftId != rightId ||
            (inner!.SpecialType is not (SpecialType.System_Int32 or SpecialType.System_Int64 or SpecialType.System_Decimal or SpecialType.System_Double)
                && CalendarArithmetic.Classify(inner) == CalendarTypeKind.None
                && !(equality && (inner.SpecialType == SpecialType.System_Boolean || inner.TypeKind == TypeKind.Enum))))
            throw new ContractLoweringException($"Unsupported lifted operand types in '{bin.Syntax}'.");

        // Datatype equality already compares the presence tag as well as the payload
        // (except DateTimeOffset which compares UtcTicks).
        if (equality && CalendarArithmetic.Classify(inner) != CalendarTypeKind.DateTimeOffset)
            return LowerBinaryOperator(bin.OperatorKind, left, right, bin.Type, gamma, bin.Syntax.GetLocation(), emitObligations);

        var cmp = LowerBinaryOperator(bin.OperatorKind,
            new SmtTypedTerm($"(opt-val-{leftId} {left.Term})", inner),
            new SmtTypedTerm($"(opt-val-{rightId} {right.Term})", inner), bin.Type, gamma, bin.Syntax.GetLocation(), emitObligations);

        if (equality)
        {
            var bothNull = $"(and {NullTest(left)} {NullTest(right)})";
            var bothPresentEq = $"(and (not {NullTest(left)}) (not {NullTest(right)}) {cmp.Term})";
            return bin.OperatorKind == BinaryOperatorKind.Equals
                ? new SmtTypedTerm($"(or {bothNull} {bothPresentEq})", bin.Type)
                : new SmtTypedTerm($"(not (or {bothNull} (and (not {NullTest(left)}) (not {NullTest(right)}) (not {cmp.Term}))))", bin.Type);
        }

        return new SmtTypedTerm($"(and (not {NullTest(left)}) (not {NullTest(right)}) {cmp.Term})", bin.Type);
    }

    private SmtTypedTerm LowerLiteralOperation(ILiteralOperation lit)
    {
        var type = lit.Type ?? _compilation.GetSpecialType(SpecialType.System_String);
        if (!lit.ConstantValue.HasValue)
            return new SmtTypedTerm("str-null", type);

        return FormatConstantScalarValue(lit.ConstantValue.Value, type);
    }

    private SmtTypedTerm FormatConstantScalarValue(object? val, ITypeSymbol type)
    {
        if (val is null)
        {
            if (_types.TryGetNullableInfo(type, out _, out var optId, out _))
                return new SmtTypedTerm($"opt-none-{optId}", type);
            return new SmtTypedTerm("str-null", _compilation.GetSpecialType(SpecialType.System_String));
        }
        if (type.TypeKind == TypeKind.Enum)
        {
            var bi = ConvertToBigInteger(val);
            return new SmtTypedTerm(CalendarArithmetic.FormatBigInt(bi), type);
        }
        if (val is bool b)
            return new SmtTypedTerm(b ? "true" : "false", type);
        if (val is int iv)
            return new SmtTypedTerm(_translator.FormatIntLiteral(iv, 32), type);
        if (val is long lv)
            return new SmtTypedTerm(_translator.FormatLongLiteral(lv), type);
        if (val is ulong ulv)
            return new SmtTypedTerm(ulv.ToString(CultureInfo.InvariantCulture), type);
        if (val is decimal dv)
            return new SmtTypedTerm(ContractExpressionTranslator.FormatDecimalLiteral(dv), type);
        if (val is double dbl)
            return new SmtTypedTerm(ContractExpressionTranslator.FormatDoubleLiteral(dbl), type);
        if (val is float flt)
            return new SmtTypedTerm(ContractExpressionTranslator.FormatDoubleLiteral(flt), type);
        if (val is string sv)
            return new SmtTypedTerm($"(str-val {ContractExpressionTranslator.EscapeSmtString(sv)})", type);
        if (val is char ch)
            return new SmtTypedTerm(((int)ch).ToString(CultureInfo.InvariantCulture), type);

        var num = ConvertToBigInteger(val);
        return new SmtTypedTerm(CalendarArithmetic.FormatBigInt(num), type);
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
        if (TryLowerCalendarBinaryOperator(kind, left, right, resultType, gamma, loc, out var calRes))
        {
            return calRes;
        }

        if (kind == BinaryOperatorKind.Add &&
            (resultType.SpecialType == SpecialType.System_String ||
             left.Type.SpecialType == SpecialType.System_String ||
             right.Type.SpecialType == SpecialType.System_String))
        {
            if (left.Type.SpecialType == SpecialType.System_String && right.Type.SpecialType == SpecialType.System_String)
            {
                var lStr = TextOrEmptyIfNull(left.Term);
                var rStr = TextOrEmptyIfNull(right.Term);
                return new SmtTypedTerm($"(str-val (str.++ {lStr} {rStr}))", _compilation.GetSpecialType(SpecialType.System_String));
            }
            var strType = _compilation.GetSpecialType(SpecialType.System_String);
            var strSsa = BindSsaValue("strcat", new SmtTypedTerm("str-null", strType), isFreshFreeSymbol: true);
            AddAssertion($"(not (= {strSsa.Term} str-null))", "StringAxiom", "concatenation non-null", loc);
            return strSsa;
        }

        if (kind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals)
        {
            if (InitializerProjection.UsesReferenceEquality(left.Type) || InitializerProjection.UsesReferenceEquality(right.Type))
            {
                var refName = InitializerProjection.UsesReferenceEquality(left.Type) ? left.Type.Name : right.Type.Name;
                throw new ContractLoweringException(
                    $"Reference equality on non-record class '{refName}' cannot be modeled as structural SMT equality; compare individual fields instead.");
            }
            ThrowIfEqualityUnmodeled(left.Type, viaOperator: false);
            ThrowIfEqualityUnmodeled(right.Type, viaOperator: false);
            var (lEq, rEq, _) = _translator.UnifyForComparison(left, right);
            var eq = SymbolEqualityComparer.Default.Equals(left.Type, right.Type) ? EqualityTerm(lEq, rEq, left.Type) : $"(= {lEq} {rEq})";
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

        if (kind is BinaryOperatorKind.Divide or BinaryOperatorKind.Remainder && sort != "Real")
        {
            return LowerIntegerDivision(kind, lTerm, rTerm, sort, resultType, gamma, loc, emitObligations);
        }

        if (kind is BinaryOperatorKind.Divide or BinaryOperatorKind.Remainder && sort == "Real")
        {
            if (emitObligations || _inQuantifierDepth > 0)
                EmitSafetyObligation("DivisionByZero", "Divisor must be non-zero", loc, gamma, $"(not (= {rTerm} 0.0))");
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
            BinaryOperatorKind.Divide => new SmtTypedTerm($"(/ {lTerm} {rTerm})", resultType),
            BinaryOperatorKind.Remainder => new SmtTypedTerm(RealRemainder(lTerm, rTerm), resultType),
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
                if (_types.TryGetNullableInfo(subject.Type, out _, out var relOptId, out var relPayloadType))
                {
                    // `x is > 5` on an int? holds only when x has a value and the payload satisfies the relation.
                    var relPayload = new SmtTypedTerm($"(opt-val-{relOptId} {subject.Term})", relPayloadType!);
                    var relCmp = LowerBinaryOperator(rp.OperatorKind, relPayload, rVal, boolType, gamma, pattern.Syntax.GetLocation(), emitObligations);
                    return new SmtTypedTerm($"(and (not (= {subject.Term} opt-none-{relOptId})) {relCmp.Term})", boolType);
                }
                return LowerBinaryOperator(rp.OperatorKind, subject, rVal, boolType, gamma, pattern.Syntax.GetLocation(), emitObligations);

            case ITypePatternOperation tp:
                return new SmtTypedTerm(LowerTypeTest(subject, tp.MatchedType, pattern), boolType);

            case IDeclarationPatternOperation dp:
                // `is var t` (and a declaration of the subject's own nullable type) matches null; any other
                // declaration pattern is a type test that null fails.
                var notNull = dp.MatchesNull ? "true" : LowerTypeTest(subject, dp.MatchedType, pattern);
                if (dp.DeclaredSymbol is ILocalSymbol locSym)
                {
                    var bound = !dp.MatchesNull && _types.TryGetNullableInfo(subject.Type, out _, out var oId, out var innerT) &&
                                !_types.TryGetNullableInfo(locSym.Type, out _, out _, out _)
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
                // Unmodelled pattern kinds (list, slice, ITuple, ...) must never be assumed to match.
                throw new ContractLoweringException($"Unsupported pattern '{pattern.Kind}' in '{pattern.Syntax}'.");
        }
    }

    /// <summary>
    /// The SMT test for "<paramref name="subject"/> is <paramref name="matched"/>": null never matches a type test,
    /// a non-null value of the subject's own (underlying) type always does. Anything else is not modelled.
    /// </summary>
    private string LowerTypeTest(SmtTypedTerm subject, ITypeSymbol? matched, IOperation pattern)
    {
        if (matched is not null)
        {
            if (subject.Type.SpecialType == SpecialType.System_String &&
                matched.SpecialType == SpecialType.System_String)
            {
                return $"(not (= {subject.Term} str-null))";
            }
            if (_types.TryGetNullableInfo(subject.Type, out _, out var optId, out var payloadType))
            {
                var matchedUnderlying = _types.TryGetNullableInfo(matched, out _, out _, out var matchedPayload) ? matchedPayload! : matched;
                if (SymbolEqualityComparer.Default.Equals(matchedUnderlying, payloadType))
                    return $"(not (= {subject.Term} opt-none-{optId}))";
            }
            else if (SymbolEqualityComparer.Default.Equals(matched, subject.Type))
            {
                // A value type, or a modelled reference type (never null in the SMT model).
                return "true";
            }
        }

        throw new ContractLoweringException($"Unsupported type pattern '{pattern.Syntax}' in specification or body.");
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

            var memberTerm = ResolveMemberAccess(target, memberName, emitObligations, inlineDepth, gamma);
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

        if (prop.IsIndexer && propRef.Instance is not null && propRef.Arguments.Length == 1)
        {
            var recv = LowerExpression(propRef.Instance, gamma, env, emitObligations, inlineDepth);
            var arg = LowerExpression(propRef.Arguments[0].Value, gamma, env, emitObligations, inlineDepth);
            if (_types.TryGetDictionaryInfo(recv.Type, out _, out var dictId, out var kType, out var vType))
            {
                var kConv = _translator.ConvertTo(arg, kType!);
                if (emitObligations || _inQuantifierDepth > 0)
                {
                    EmitSafetyObligation("CollectionBounds", $"Dictionary key exists for indexer '{propRef.Syntax}'", propRef.Syntax.GetLocation(), gamma,
                        $"(select (map_has_{dictId} {recv.Term}) {kConv.Term})");
                }
                var valTerm = new SmtTypedTerm($"(select (map_data_{dictId} {recv.Term}) {kConv.Term})", vType!);
                EmitOnDemandElementAssumptions($"(select (map_has_{dictId} {recv.Term}) {kConv.Term})", valTerm, propRef.Syntax.GetLocation());
                return valTerm;
            }
            if (_types.TryGetSequenceInfo(recv.Type, out _, out var seqId, out var eType, out var isImmArr))
            {
                if (isImmArr)
                {
                    EmitImmutableArrayNonDefaultObligation(recv, $"ImmutableArray must not be default for indexer '{propRef.Syntax}'", propRef.Syntax.GetLocation(), gamma);
                }
                var idxTerm = _translator.IntTermForIndex(arg);
                var boundsCond = $"(and (>= {idxTerm} 0) (< {idxTerm} (seq_count_{seqId} {recv.Term})))";
                if (emitObligations || _inQuantifierDepth > 0)
                {
                    EmitSafetyObligation("CollectionBounds", $"Sequence index within bounds for '{propRef.Syntax}'", propRef.Syntax.GetLocation(), gamma, boundsCond);
                }
                var elemVal = new SmtTypedTerm($"(select (seq_elems_{seqId} {recv.Term}) {idxTerm})", eType!);
                EmitOnDemandElementAssumptions(boundsCond, elemVal, propRef.Syntax.GetLocation());
                return elemVal;
            }
        }

        if (propRef.Instance is null)
        {
            if (prop.ContainingType is not null &&
                TryLowerCalendarStaticMember(prop.ContainingType, prop.Name, prop.Type, out var calStatic))
            {
                return calStatic;
            }
            var containing = prop.ContainingType?.OriginalDefinition.ToDisplayString() ?? string.Empty;
            if (containing.StartsWith("System.Collections.Immutable.", StringComparison.Ordinal) && prop.Name == "Empty")
            {
                var empty = new SmtTypedTerm(_types.GetEmptySequenceTerm(prop.Type), prop.Type);
                if (_types.TryGetSequenceInfo(prop.Type, out _, out _, out _, out _))
                {
                    _knownConcreteSequences[empty.Term] = Array.Empty<SmtTypedTerm>();
                }
                return empty;
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
            if (_types.TryGetSequenceInfo(recv.Type, out _, out _, out _, out var isImmArr) &&
                isImmArr && prop.Name is "Length" or "Count" or "IsEmpty")
            {
                EmitImmutableArrayNonDefaultObligation(recv, $"ImmutableArray must not be default for '.{prop.Name}'", propRef.Syntax.GetLocation(), gamma);
            }
            if (emitObligations || _inQuantifierDepth > 0)
            {
                if (recv.Type.SpecialType == SpecialType.System_String)
                {
                    EmitStringNonNullObligation(recv, $"Receiver must be non-null for '{propRef.Syntax}'", propRef.Syntax.GetLocation(), gamma);
                }
                else if (prop.Name == "Value" && _types.TryGetNullableInfo(recv.Type, out _, out var valOptId, out _))
                {
                    EmitSafetyObligation("Precondition", $"Nullable must have a value for '{propRef.Syntax}'",
                        propRef.Syntax.GetLocation(), gamma, $"(not (= {recv.Term} opt-none-{valOptId}))");
                }
            }
            return ResolveMemberAccess(recv, prop.Name, emitObligations, inlineDepth, gamma);
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
        if (fieldRef.Instance is null)
        {
            if (field.ContainingType is not null &&
                TryLowerCalendarStaticMember(field.ContainingType, field.Name, field.Type, out var calStatic))
            {
                return calStatic;
            }
            var containing = field.ContainingType?.OriginalDefinition.ToDisplayString() ?? string.Empty;
            if (containing.StartsWith("System.Collections.Immutable.", StringComparison.Ordinal) && field.Name == "Empty")
            {
                var empty = new SmtTypedTerm(_types.GetEmptySequenceTerm(field.Type), field.Type);
                if (_types.TryGetSequenceInfo(field.Type, out _, out _, out _, out _))
                {
                    _knownConcreteSequences[empty.Term] = Array.Empty<SmtTypedTerm>();
                }
                return empty;
            }
            if (field.ContainingType?.ToDisplayString() == "System.Guid" && field.Name == "Empty")
            {
                return new SmtTypedTerm("0", field.Type);
            }
            if (field.HasConstantValue)
            {
                return FormatConstantScalarValue(field.ConstantValue, field.Type);
            }
            if (field.ContainingType?.OriginalDefinition.ToDisplayString() == "Verifiable.Contracts.Unit" && field.Name == "Value")
            {
                return new SmtTypedTerm(_types.GetDefaultTerm(field.Type), field.Type);
            }
            if (!emitObligations)
                throw new ContractLoweringException($"Unsupported static field '{field.Name}' in specification.");
            if (field.IsReadOnly && _inQuantifierDepth == 0)
            {
                // A static readonly field never changes after its initializer, so every read sees the same unknown value.
                if (!_staticReadonlyFields.TryGetValue(field, out var cached))
                {
                    cached = BindSsaValue("fld", new SmtTypedTerm(_types.GetDefaultTerm(field.Type), field.Type), isFreshFreeSymbol: true);
                    _staticReadonlyFields[field] = cached;
                }
                return cached;
            }
            return BindSsaValue("fld", new SmtTypedTerm(_types.GetDefaultTerm(field.Type), field.Type), isFreshFreeSymbol: true);
        }

        var recv = LowerExpression(fieldRef.Instance, gamma, env, emitObligations, inlineDepth);
        return ResolveMemberAccess(recv, field.Name, emitObligations, inlineDepth, gamma);
    }

    private SmtTypedTerm ResolveMemberAccess(SmtTypedTerm receiver, string memberName, bool emitObligations, int inlineDepth, string gamma = "true")
    {
        if (_translator.TryTranslateMemberOnReceiver(receiver, memberName) is { } direct)
        {
            if (memberName == "Length" && receiver.Type.SpecialType == SpecialType.System_String &&
                _types.GetSort(direct.Type) == "(_ BitVec 32)")
            {
                // Under [CheckedArithmetic] the length is a 32-bit vector; a .NET string holds at most 0x3FFFFFDF code
                // units, so the conversion from Z3's unbounded length never wraps for a real string.
                AddAssertion($"(or (= {receiver.Term} str-null) (<= (str.len (str-text {receiver.Term})) 1073741791))",
                    "StringAxiom", "string length bound", Location.None);
            }
            return direct;
        }

        var prop = receiver.Type.GetMembers(memberName).OfType<IPropertySymbol>().FirstOrDefault();
        if (prop is not null && inlineDepth < 8 &&
            SmtTypeSystem.TryExtractSingleReturnOperation(_compilation, prop, out _, out var bodyOp))
        {
            var propEnv = new BlockEnv { ThisOverride = receiver };
            // A computed property is user code run at the access: its checks (a division, an index) belong to the caller.
            return LowerExpression(bodyOp!, gamma, propEnv, emitObligations, inlineDepth + 1);
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
                if (emitObligations &&
                    (f.IsNonDefault || _nonDefaultImmutableArrays) &&
                    _types.TryGetSequenceInfo(f.Type, out _, out var fSeqId, out _, out var fIsImmArr) &&
                    fIsImmArr)
                {
                    EmitSafetyObligation(
                        "TypeInvariant",
                        $"[NonDefault] field '{withOp.Type!.Name}.{f.Name}'",
                        withOp.Syntax.GetLocation(),
                        gamma,
                        $"(not (seq_is_default_{fSeqId} {ov.Term}))",
                        $"Field '{f.Name}' on '{withOp.Type!.Name}' must be an initialized ImmutableArray (not default).");
                }
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
        if (TryLowerCalendarObjectCreation(objCreate, gamma, env, emitObligations, inlineDepth, out var calObj))
        {
            return calObj;
        }

        var targetType = objCreate.Type!;
        if (!_types.TryGetComposite(targetType, out var comp))
        {
            return new SmtTypedTerm(_types.GetDefaultTerm(targetType), targetType);
        }

        Dictionary<string, SmtTypedTerm>? ctorFields = null;
        if (!InitializerProjection.IsSupportedProjection(objCreate, _types, out var projErr))
        {
            // Keep compatibility with existing constrained-struct/internal constructors when not a custom DTO projection
            if (objCreate.Initializer is not null || InitializerProjection.UsesReferenceEquality(targetType))
            {
                throw new ContractLoweringException(projErr!);
            }
            if (UnmodeledConstructionReason(objCreate) is { } unmodeled)
            {
                // User code (a constructor body, a property initializer): lower it, or refuse (see CfgSsaLowerer.Equality.cs).
                ctorFields = LowerSourceConstructor(objCreate, comp!, unmodeled, gamma, env, emitObligations, inlineDepth);
            }
        }

        var argMap = ctorFields ?? new Dictionary<string, SmtTypedTerm>(StringComparer.OrdinalIgnoreCase);
        var ctorArgs = new SmtTypedTerm?[objCreate.Constructor?.Parameters.Length ?? 0];
        // With an explicit constructor body, LowerSourceConstructor has already bound the fields (and checked the
        // constructor's Spec.Requires). The arguments are still lowered here so their validity can be checked.
        foreach (var arg in objCreate.Arguments)
        {
            if (arg.Parameter is not null)
            {
                var v = LowerExpression(arg.Value, gamma, env, emitObligations, inlineDepth);
                var converted = _translator.ConvertTo(v, arg.Parameter.Type);
                if (ctorFields is null) argMap[arg.Parameter.Name] = converted;
                if (arg.Parameter.Ordinal < ctorArgs.Length) ctorArgs[arg.Parameter.Ordinal] = converted;
            }
        }

        if (emitObligations)
        {
            CheckConstructorCall(objCreate, ctorArgs, gamma, requiresAlreadyChecked: ctorFields is not null);
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

        var args = new List<string>(comp.Fields.Count);
        foreach (var f in comp.Fields)
        {
            var fieldTerm = argMap.TryGetValue(f.Name, out var v)
                ? _translator.ConvertTo(v, f.Type).Term
                : _types.GetDefaultTerm(f.Type);
            if (emitObligations &&
                (f.IsNonDefault || _nonDefaultImmutableArrays) &&
                _types.TryGetSequenceInfo(f.Type, out _, out var fSeqId, out _, out var fIsImmArr) &&
                fIsImmArr)
            {
                EmitSafetyObligation(
                    "TypeInvariant",
                    $"[NonDefault] field '{targetType.Name}.{f.Name}'",
                    objCreate.Syntax.GetLocation(),
                    gamma,
                    $"(not (seq_is_default_{fSeqId} {fieldTerm}))",
                    $"Field '{f.Name}' on '{targetType.Name}' must be an initialized ImmutableArray (not default).");
            }
            args.Add(fieldTerm);
        }

        var constructed = new SmtTypedTerm($"({comp.ConstructorName} {string.Join(" ", args)})", targetType);
        return BindSsaValue("new_" + SmtTypeSystem.SymbolPart(targetType.Name), constructed);
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
        if (_types.TryGetSequenceInfo(t, out var seqSort, out var seqId, out var elemType, out var isImmArr))
        {
            var cur = new SmtTypedTerm(_types.GetEmptySequenceTerm(t), t);
            var concrete = new List<SmtTypedTerm>(colExpr.Elements.Length);
            var i = 0;
            foreach (var elemOp in colExpr.Elements)
            {
                if (elemOp is ISpreadOperation)
                {
                    throw new ContractLoweringException($"Spread element in collection expression '{colExpr.Syntax}' is not supported.");
                }
                var ev = _translator.ConvertTo(LowerExpression(elemOp, gamma, env, emitObligations, inlineDepth), elemType!);
                concrete.Add(ev);
                cur = new SmtTypedTerm(
                    _types.BuildSequenceTerm(seqSort, $"(store (seq_elems_{seqId} {cur.Term}) {i} {ev.Term})", (i + 1).ToString(CultureInfo.InvariantCulture), isImmArr, isDefault: false),
                    t);
                i++;
            }
            var bound = BindSsaValue("seq", cur);
            _knownConcreteSequences[bound.Term] = concrete;
            return bound;
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
                    throw new ContractLoweringException("Spec.Result<TResult>() can only be used inside Spec.Ensures(...) or Spec.EnsuresWhen(...).");
                }
                if (method.TypeArguments.Length == 1 &&
                    !SymbolEqualityComparer.Default.Equals(method.TypeArguments[0], env.SpecResult.Type))
                {
                    throw new ContractLoweringException($"Spec.Result<{method.TypeArguments[0].Name}>() does not match method return type '{env.SpecResult.Type.Name}'.");
                }
                return env.SpecResult;
            }

            if (method.Name == "Implies" && inv.Arguments.Length == 2)
            {
                var ant = LowerExpression(GetOrderedArgOp(inv, 0), gamma, env, emitObligations, inlineDepth);
                // In code (obligations emitted) C# evaluates both arguments, so the consequent runs under the caller's path
                // condition; only in a specification is Implies a logical connective guarding its consequent.
                var consGamma = emitObligations ? gamma : CombineAnd(gamma, ant.Term);
                var cons = LowerExpression(GetOrderedArgOp(inv, 1), consGamma, env, emitObligations, inlineDepth);
                return new SmtTypedTerm($"(=> {ant.Term} {cons.Term})", _compilation.GetSpecialType(SpecialType.System_Boolean));
            }

            if (method.Name is "ForAll" or "Exists" && inv.Arguments.Length == 3)
            {
                return LowerSpecQuantifier(inv, isForAll: method.Name == "ForAll", gamma, env, emitObligations, inlineDepth);
            }

            // Requires / RequiresWhen / Ensures / EnsuresWhen / LoopInvariant / Decreases are compile-time annotations processed outside statement simulation
            return new SmtTypedTerm("0", _compilation.GetSpecialType(SpecialType.System_Int32));
        }

        // 0a. Regex.IsMatch (must intercept BEFORE eagerly lowering inv.Instance so [GeneratedRegex] factory isn't treated as a helper call)
        if (TryLowerRegexIsMatch(inv, gamma, env, emitObligations, inlineDepth, out var regexResult))
        {
            return regexResult;
        }

        // 0b. Pure LINQ over immutable sequences (must intercept BEFORE eagerly lowering lambda arguments)
        if (TryLowerLinqInvocation(inv, gamma, env, emitObligations, inlineDepth, out var linqResult))
        {
            return linqResult;
        }

        // 0c. String intrinsics (intercept before eager arg lowering so string.Join params arrays/spans are handled cleanly)
        if (containing?.SpecialType == SpecialType.System_String || StringIntrinsics.IsCharToString(method))
        {
            var strRecv = inv.Instance is not null ? LowerExpression(inv.Instance, gamma, env, emitObligations, inlineDepth) : null;
            if (TryLowerStringIntrinsic(inv, strRecv, gamma, env, emitObligations, inlineDepth, out var strResult))
            {
                return strResult;
            }
        }

        // 0d. ImmutableList.Create / ImmutableArray.Create with params elements
        if (containing?.OriginalDefinition.ToDisplayString() is
            "System.Collections.Immutable.ImmutableList" or "System.Collections.Immutable.ImmutableArray" &&
            method.Name == "Create" &&
            _types.TryGetSequenceInfo(method.ReturnType, out var createSeqSort, out var createSeqId, out var createElemType, out var createIsImmArr))
        {
            if (inv.Arguments.Length == 0)
            {
                var empty = new SmtTypedTerm(_types.GetEmptySequenceTerm(method.ReturnType), method.ReturnType);
                _knownConcreteSequences[empty.Term] = Array.Empty<SmtTypedTerm>();
                return empty;
            }
            if (TryExtractImmutableCreateElements(inv, out var createOps))
            {
                var cur = new SmtTypedTerm(_types.GetEmptySequenceTerm(method.ReturnType), method.ReturnType);
                var concrete = new List<SmtTypedTerm>(createOps.Count);
                for (var i = 0; i < createOps.Count; i++)
                {
                    var ev = _translator.ConvertTo(LowerExpression(createOps[i], gamma, env, emitObligations, inlineDepth), createElemType!);
                    concrete.Add(ev);
                    cur = new SmtTypedTerm(
                        _types.BuildSequenceTerm(createSeqSort, $"(store (seq_elems_{createSeqId} {cur.Term}) {i} {ev.Term})", (i + 1).ToString(CultureInfo.InvariantCulture), createIsImmArr, isDefault: false),
                        method.ReturnType);
                }
                var bound = BindSsaValue("seqCreate", cur);
                _knownConcreteSequences[bound.Term] = concrete;
                return bound;
            }
        }

        var recvTerm = inv.Instance is not null ? LowerExpression(inv.Instance, gamma, env, emitObligations, inlineDepth) : null;

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
                return BindSsaValue("ref_" + SmtTypeSystem.SymbolPart(containing.Name),
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
                if (emitObligations || _inQuantifierDepth > 0)
                {
                    EmitSafetyObligation("CollectionBounds", "Key not already present before ImmutableDictionary.Add", loc, gamma,
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
        if (recvTerm is not null && _types.TryGetSequenceInfo(recvTerm.Type, out var seqSort, out var seqId, out var seqElemType, out var seqIsImmArr))
        {
            if (method.Name == "Add" && argValues.Count == 1)
            {
                if (seqIsImmArr)
                {
                    EmitImmutableArrayNonDefaultObligation(recvTerm, $"ImmutableArray must not be default before Add in '{inv.Syntax}'", loc, gamma);
                }
                var v = _translator.ConvertTo(argValues[0], seqElemType!);
                var updated = new SmtTypedTerm(
                    _types.BuildSequenceTerm(seqSort, $"(store (seq_elems_{seqId} {recvTerm.Term}) (seq_count_{seqId} {recvTerm.Term}) {v.Term})", $"(+ (seq_count_{seqId} {recvTerm.Term}) 1)", seqIsImmArr, isDefault: false),
                    recvTerm.Type);
                var bound = BindSsaValue("seqAdd", updated);
                if (_knownConcreteSequences.TryGetValue(recvTerm.Term, out var prevElems))
                {
                    var nextElems = new List<SmtTypedTerm>(prevElems.Count + 1);
                    nextElems.AddRange(prevElems);
                    nextElems.Add(v);
                    _knownConcreteSequences[bound.Term] = nextElems;
                }
                return bound;
            }
            if (method.Name == "SetItem" && argValues.Count == 2)
            {
                if (seqIsImmArr)
                {
                    EmitImmutableArrayNonDefaultObligation(recvTerm, $"ImmutableArray must not be default before SetItem in '{inv.Syntax}'", loc, gamma);
                }
                var idxTerm = _translator.IntTermForIndex(argValues[0]);
                var v = _translator.ConvertTo(argValues[1], seqElemType!);
                if (emitObligations || _inQuantifierDepth > 0)
                {
                    EmitSafetyObligation("CollectionBounds", "Sequence index within bounds for SetItem", loc, gamma,
                        $"(and (>= {idxTerm} 0) (< {idxTerm} (seq_count_{seqId} {recvTerm.Term})))");
                }
                var updated = new SmtTypedTerm(
                    _types.BuildSequenceTerm(seqSort, $"(store (seq_elems_{seqId} {recvTerm.Term}) {idxTerm} {v.Term})", $"(seq_count_{seqId} {recvTerm.Term})", seqIsImmArr, isDefault: false),
                    recvTerm.Type);
                return BindSsaValue("seqSet", updated);
            }
            if (method.Name == "RemoveAt" && argValues.Count == 1)
            {
                if (seqIsImmArr && (emitObligations || _inQuantifierDepth > 0))
                {
                    EmitImmutableArrayNonDefaultObligation(recvTerm, $"ImmutableArray must not be default before RemoveAt in '{inv.Syntax}'", loc, gamma);
                }
                var idxTerm = _translator.IntTermForIndex(argValues[0]);
                var inBounds = $"(and (>= {idxTerm} 0) (< {idxTerm} (seq_count_{seqId} {recvTerm.Term})))";
                if (emitObligations || _inQuantifierDepth > 0)
                {
                    EmitSafetyObligation("CollectionBounds", "Sequence index within bounds for RemoveAt", loc, gamma, inBounds);
                }
                var remElems = $"(lambda ((__ver_rm Int)) (ite (< __ver_rm {idxTerm}) (select (seq_elems_{seqId} {recvTerm.Term}) __ver_rm) (select (seq_elems_{seqId} {recvTerm.Term}) (+ __ver_rm 1))))";
                var updated = new SmtTypedTerm(
                    _types.BuildSequenceTerm(seqSort, remElems, $"(- (seq_count_{seqId} {recvTerm.Term}) 1)", seqIsImmArr, isDefault: false),
                    recvTerm.Type);
                return BindSsaValue("seqRem", updated);
            }
            if (method.Name == "Insert" && argValues.Count == 2)
            {
                if (seqIsImmArr && (emitObligations || _inQuantifierDepth > 0))
                {
                    EmitImmutableArrayNonDefaultObligation(recvTerm, $"ImmutableArray must not be default before Insert in '{inv.Syntax}'", loc, gamma);
                }
                var idxTerm = _translator.IntTermForIndex(argValues[0]);
                var v = _translator.ConvertTo(argValues[1], seqElemType!);
                var inBounds = $"(and (>= {idxTerm} 0) (<= {idxTerm} (seq_count_{seqId} {recvTerm.Term})))";
                if (emitObligations || _inQuantifierDepth > 0)
                {
                    EmitSafetyObligation("CollectionBounds", "Sequence index within bounds for Insert", loc, gamma, inBounds);
                }
                var insElems = $"(lambda ((__ver_ins Int)) (ite (< __ver_ins {idxTerm}) (select (seq_elems_{seqId} {recvTerm.Term}) __ver_ins) (ite (= __ver_ins {idxTerm}) {v.Term} (select (seq_elems_{seqId} {recvTerm.Term}) (- __ver_ins 1)))))";
                var updated = new SmtTypedTerm(
                    _types.BuildSequenceTerm(seqSort, insElems, $"(+ (seq_count_{seqId} {recvTerm.Term}) 1)", seqIsImmArr, isDefault: false),
                    recvTerm.Type);
                return BindSsaValue("seqIns", updated);
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
            "System.Collections.Immutable.ImmutableDictionary" or
            "System.Collections.Immutable.ImmutableHashSet" &&
            method.Name == "Create" && argValues.Count == 0)
        {
            return new SmtTypedTerm(_types.GetDefaultTerm(method.ReturnType), method.ReturnType);
        }

        // 7. Decimal rounding & Calendar arithmetic
        if (TryLowerDecimalRounding(inv, argValues, loc, out var roundResult))
        {
            return roundResult;
        }

        if (TryLowerCalendarInvocation(inv, recvTerm, argValues, gamma, env, emitObligations, inlineDepth, out var calResult))
        {
            return calResult;
        }

        // 8. Math / decimal helpers
        if (containing?.SpecialType is SpecialType.System_Decimal || containing?.ToDisplayString() == "System.Math")
        {
            if (method.Name == "Abs" && argValues.Count == 1)
            {
                var a = _translator.UnwrapRefinedIfApplicable(argValues[0]);
                var sort = _types.GetSort(a.Type);
                var zero = sort == "Real" ? "0.0" : _translator.FormatIntLiteral(0, sort == "(_ BitVec 64)" ? 64 : 32);
                var lt = ContractExpressionTranslator.IsBitVecSort(sort) ? "bvslt" : "<";
                var neg = ContractExpressionTranslator.IsBitVecSort(sort) ? "bvneg" : "-";
                if (emitObligations && sort is "(_ BitVec 32)" or "(_ BitVec 64)")
                {
                    // Math.Abs(MinValue) has no positive counterpart and throws OverflowException.
                    var minVal = sort == "(_ BitVec 32)" ? "(_ bv2147483648 32)" : "(_ bv9223372036854775808 64)";
                    EmitSafetyObligation("Overflow", "Math.Abs(MinValue) throws OverflowException", loc, gamma, $"(not (= {a.Term} {minVal}))");
                }
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
            if (InitializerProjection.UsesReferenceEquality(recvTerm.Type) || InitializerProjection.UsesReferenceEquality(argValues[0].Type))
            {
                var refName = InitializerProjection.UsesReferenceEquality(recvTerm.Type) ? recvTerm.Type.Name : argValues[0].Type.Name;
                throw new ContractLoweringException(
                    $"Reference equality via Equals on non-record class '{refName}' cannot be modeled as structural SMT equality; compare individual fields instead.");
            }
            ThrowIfEqualityUnmodeled(recvTerm.Type, viaOperator: false);
            ThrowIfEqualityUnmodeled(argValues[0].Type, viaOperator: false);
            if ((emitObligations || _inQuantifierDepth > 0) && IsStringTyped(inv.Instance, recvTerm))
            {
                EmitStringNonNullObligation(recvTerm, $"Receiver must be non-null for '{inv.Syntax}'", loc, gamma);
            }
            var (l, r, _) = _translator.UnifyForComparison(recvTerm, argValues[0]);
            return new SmtTypedTerm(SymbolEqualityComparer.Default.Equals(recvTerm.Type, argValues[0].Type)
                ? EqualityTerm(l, r, recvTerm.Type)
                : $"(= {l} {r})", _compilation.GetSpecialType(SpecialType.System_Boolean));
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

    /// <summary>True when the receiver is a C# string, even if it was converted to an interface such as IEquatable&lt;string&gt;.</summary>
    private static bool IsStringTyped(IOperation? instance, SmtTypedTerm recvTerm)
    {
        if (recvTerm.Type.SpecialType == SpecialType.System_String)
            return true;
        var cur = instance;
        while (cur is IConversionOperation conv)
        {
            cur = conv.Operand;
            if (cur.Type?.SpecialType == SpecialType.System_String)
                return true;
        }
        return false;
    }

    private SmtTypedTerm LowerHelperCall(IMethodSymbol method, SmtTypedTerm? receiver, IReadOnlyList<SmtTypedTerm> args,
        string gamma, Location loc, bool implementationCall, int inlineDepth)
    {
        var specs = ExtractMethodSpecs(_compilation, method, default);
        _errors.AddRange(specs.Errors);
        var calleeEnv = new BlockEnv { ThisOverride = receiver };
        for (var i = 0; i < method.Parameters.Length && i < args.Count; i++)
        {
            calleeEnv.Vars[method.Parameters[i]] = args[i];
            if (IsClosedEnum(method.Parameters[i].Type, out var paramEnum, out var paramEnumVals))
            {
                EmitSafetyObligation("Precondition",
                    $"Closed enum argument '{method.Parameters[i].Name}' for '{method.ContainingType?.Name}.{method.Name}'",
                    loc, gamma, BuildClosedEnumFormula(args[i].Term, paramEnumVals),
                    $"Argument passed to closed enum parameter '{paramEnum!.Name}' must be a declared member.");
            }
            if (VerifiableSymbolHelpers.IsNonDefaultParameter(method.Parameters[i], _nonDefaultImmutableArrays) &&
                _types.TryGetSequenceInfo(args[i].Type, out _, out var argSeqId, out _, out var argIsImmArr) &&
                argIsImmArr)
            {
                EmitSafetyObligation("Precondition",
                    $"[NonDefault] argument '{method.Parameters[i].Name}' for '{method.ContainingType?.Name}.{method.Name}'",
                    loc, gamma, $"(not (seq_is_default_{argSeqId} {args[i].Term}))",
                    $"Parameter '{method.Parameters[i].Name}' is marked [NonDefault] and cannot receive default(ImmutableArray).");
            }
        }

        foreach (var req in specs.Requires)
        {
            if (req.StubMethod is not null)
            {
                CrossAssemblyContractCache.BindStubParameters(calleeEnv.Vars, method, req.StubMethod, args);
            }
        }

        foreach (var ens in specs.Ensures)
        {
            if (ens.StubMethod is not null)
            {
                CrossAssemblyContractCache.BindStubParameters(calleeEnv.Vars, method, ens.StubMethod, args);
            }
        }

        EmitArgumentValidityObligations(method, args, gamma, loc);

        var calleeRequires = CheckMethodRequires(method, specs, calleeEnv, gamma, loc);

        var definitional = VerifiableSymbolHelpers.GetScope(method) == VerificationScope.Verifiable &&
            !VerifiableSymbolHelpers.HasAttribute(method, VerifiableSymbolHelpers.GhostAttr);
        SmtTypedTerm? definition = null;
        if ((!implementationCall || definitional) && !method.ReturnsVoid &&
            !VerifiableSymbolHelpers.HasAttribute(method, VerifiableSymbolHelpers.PureAxiomAttr) &&
            HasCheckedArithmetic(method) == _types.UseBitVec &&
            !method.IsGenericMethod && method.ContainingType is { IsGenericType: false } &&
            (!method.IsVirtual && !method.IsOverride || method.IsSealed || method.ContainingType.IsSealed) &&
            !method.ReturnsByRef && !method.ReturnsByRefReadonly && method.Parameters.All(p => p.RefKind == RefKind.None) &&
            inlineDepth < 8 && SmtTypeSystem.TryExtractSingleReturnOperation(_compilation, method, out _, out var body, out var bodyStubMethod, allowLeadingContracts: true) &&
            CanUseHelperBody(body!))
        {
            if (bodyStubMethod is not null)
            {
                CrossAssemblyContractCache.BindStubParameters(calleeEnv.Vars, method, bodyStubMethod, args);
            }

            try
            {
                definition = _translator.ConvertTo(LowerExpression(body!, gamma, calleeEnv, emitObligations: false, inlineDepth + 1), method.ReturnType);
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

        var retTerm = BindSsaValue("call_" + SmtTypeSystem.SymbolPart(method.Name),
            new SmtTypedTerm(_types.GetDefaultTerm(method.ReturnType), method.ReturnType),
            isFreshFreeSymbol: true);

        if (definition is not null)
            AddAssertion($"(=> {gamma} (= {retTerm.Term} {definition.Term}))", "HelperBody", method.Name, loc);
        EmitTypeAssumptions(retTerm, loc);

        if (!_nonDefaultImmutableArrays &&
            VerifiableSymbolHelpers.IsNonDefaultReturn(method, globalNonDefault: false) &&
            _types.TryGetSequenceInfo(retTerm.Type, out _, out var retSeqId, out _, out var retIsImmArr) &&
            retIsImmArr)
        {
            AddAssertion(
                $"(=> {gamma} (not (seq_is_default_{retSeqId} {retTerm.Term})))",
                "NonDefault",
                $"[NonDefault] return of '{method.ContainingType?.Name}.{method.Name}'",
                loc);
        }

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
                    var effectiveGamma = gamma;
                    if (ens.GuardCondition is not null)
                    {
                        var guardTerm = LowerExpression(ens.GuardCondition, gamma, calleeEnv, emitObligations: false);
                        effectiveGamma = CombineAnd(gamma, guardTerm.Term);
                    }
                    var ensTerm = LowerExpression(ens.BodyExpr, effectiveGamma, calleeEnv, emitObligations: false);
                    var ensLoc = ens.Location != Location.None ? ens.Location : loc;
                    _calleeGuarantees.Add(new CalleeGuarantee(
                        $"{method.ContainingType?.Name}.{method.Name}: {ens.DisplayText}",
                        ens.Because,
                        loc,
                        ensLoc,
                        calleeRequires.Aggregate(effectiveGamma, CombineAnd),
                        ensTerm.Term,
                        _assertions.Count,
                        _obligations.Count));
                    AddAssertion(
                        $"(=> {effectiveGamma} {ensTerm.Term})",
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
        !body.DescendantsAndSelf().Any(op =>
            (op is IAssignmentOperation assign && !IsObjectOrWithInitializerAssignment(assign)) ||
            op is IIncrementOrDecrementOperation ||
            op is ILocalReferenceOperation ||
            (op is IAnonymousFunctionOperation anon && !IsLinqCallbackAnonymousFunction(anon)) ||
            op is IConversionOperation { OperatorMethod: not null } ||
            op is IBinaryOperation { OperatorMethod: { } binary } && !IsBuiltInValueOperator(binary) ||
            op is IUnaryOperation { OperatorMethod: { } unary } && !IsBuiltInValueOperator(unary) ||
            op is IObjectCreationOperation obj &&
                CalendarArithmetic.Classify(obj.Type) == CalendarTypeKind.None &&
                !InitializerProjection.IsSupportedProjection(obj, _types, out _));

    private static bool IsObjectOrWithInitializerAssignment(IAssignmentOperation assign) =>
        assign is ISimpleAssignmentOperation && assign.Parent is IObjectOrCollectionInitializerOperation;

    private static bool IsLinqCallbackAnonymousFunction(IAnonymousFunctionOperation anon)
    {
        for (var cur = anon.Parent; cur is not null; cur = cur.Parent)
        {
            if (cur is IArgumentOperation arg && arg.Parent is IInvocationOperation inv)
            {
                var orig = inv.TargetMethod.ContainingType?.OriginalDefinition.ToDisplayString();
                if (orig is "System.Linq.Enumerable" or "System.Linq.ImmutableArrayExtensions")
                {
                    return true;
                }
                return orig == VerifiableSymbolHelpers.SpecTypeName && inv.TargetMethod.Name is "ForAll" or "Exists";
            }
            if (cur is not (IDelegateCreationOperation or IConversionOperation))
            {
                return false;
            }
        }
        return false;
    }

    private static bool IsBuiltInValueOperator(IMethodSymbol method) =>
        method.ContainingType.SpecialType is SpecialType.System_Decimal or SpecialType.System_String ||
        CalendarArithmetic.Classify(method.ContainingType) != CalendarTypeKind.None ||
        method.IsImplicitlyDeclared && method.ContainingType.IsRecord;

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

        foreach (var req in calleeSpecs.Requires)
        {
            if (req.StubMethod is not null)
            {
                CrossAssemblyContractCache.BindStubParameters(calleeEnv.Vars, callee, req.StubMethod, args);
            }
        }

        CheckMethodRequires(callee, calleeSpecs, calleeEnv, gamma, callLoc);
    }

    /// <summary>Emits a goal for each callee precondition and returns them as formulas, guards included.</summary>
    private List<string> CheckMethodRequires(IMethodSymbol callee, MethodSpecs calleeSpecs, BlockEnv calleeEnv, string gamma, Location callLoc)
    {
        var lowered = new List<string>();
        foreach (var req in calleeSpecs.Requires)
        {
            try
            {
                var effectiveGamma = gamma;
                string? guard = null;
                if (req.GuardCondition is not null)
                {
                    guard = LowerExpression(req.GuardCondition, gamma, calleeEnv, emitObligations: false).Term;
                    effectiveGamma = CombineAnd(gamma, guard);
                }

                var reqTerm = LowerExpression(req.Condition, effectiveGamma, calleeEnv, emitObligations: false);
                lowered.Add(guard is null ? reqTerm.Term : $"(=> {guard} {reqTerm.Term})");
                var clauses = ExtractGoalClauses(req.Condition, effectiveGamma, calleeEnv);
                AddObligation(
                    "Precondition",
                    $"{req.DisplayText} on {callee.ContainingType?.Name}.{callee.Name}",
                    callLoc,
                    effectiveGamma,
                    reqTerm.Term,
                    req.Because,
                    contractLocation: req.Location,
                    contractLocationLabel: $"The callee precondition on `{callee.ContainingType?.Name}.{callee.Name}` is declared here:",
                    goalClauses: clauses);
            }
            catch (ContractLoweringException ex)
            {
                _errors.Add((req.DisplayText, callLoc, ex.Message));
            }
        }

        return lowered;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Compilation, object> RegexUseCache = new();

    /// <summary>True when the program mentions <c>Regex</c> at all, so that regex-related string assumptions are worth their cost.</summary>
    private bool CompilationUsesRegex() =>
        (bool)RegexUseCache.GetValue(_compilation, c =>
            c.SyntaxTrees.Any(t => t.GetText().ToString().IndexOf("Regex", StringComparison.Ordinal) >= 0));

    private void EmitTypeAssumptions(SmtTypedTerm value, Location loc)
    {
        if (value.Type.SpecialType == SpecialType.System_String)
        {
            // A .NET string is a sequence of UTF-16 code units, but Z3's characters reach U+2FFFF; without this a
            // regex over a full-BMP class could be "refuted" by a character no string can hold.
            if (CompilationUsesRegex())
            {
                AddAssertion(RegexModel.BmpStringAssumption(value.Term), "StringAxiom", "string characters are UTF-16 code units", loc);
            }
            return;
        }

        if (value.Type.TypeKind == TypeKind.Enum &&
            TryGetIntegralBounds(value.Type, out var eMin, out var eMax, out _, out _))
        {
            AddAssertion(
                $"(and (>= {value.Term} {CalendarArithmetic.FormatBigInt(eMin)}) (<= {value.Term} {CalendarArithmetic.FormatBigInt(eMax)}))",
                "EnumRange", $"{value.Type.Name} underlying range", loc);

            if (IsClosedEnum(value.Type, out _, out var closedVals))
            {
                AddAssertion(BuildClosedEnumFormula(value.Term, closedVals),
                    "ClosedEnum", $"{value.Type.Name} declared members", loc);
            }
            return;
        }

        var calKind = CalendarArithmetic.Classify(value.Type);
        if (calKind != CalendarTypeKind.None)
        {
            EmitCalendarAssumptions(value.Term, calKind, guardCond: null, loc);
            return;
        }

        if (_types.TryGetNullableInfo(value.Type, out _, out var optId, out var optInner))
        {
            EmitOnDemandElementAssumptions(
                $"(not (= {value.Term} opt-none-{optId}))",
                new SmtTypedTerm($"(opt-val-{optId} {value.Term})", optInner!),
                loc);
            return;
        }

        if (_types.TryGetDictionaryInfo(value.Type, out _, out var dictId, out var kType, out _))
        {
            var kSort = _types.GetSort(kType!);
            AddAssertion($"(>= (map_count_{dictId} {value.Term}) 0)", "CollectionAxiom", $"{value.Type.Name}.Count >= 0", loc);
            AddAssertion($"(= (= (map_count_{dictId} {value.Term}) 0) (= (map_has_{dictId} {value.Term}) ((as const (Array {kSort} Bool)) false)))", "CollectionAxiom", $"{value.Type.Name} empty consistency", loc);
        }
        else if (_types.TryGetSequenceInfo(value.Type, out _, out var seqId, out var seqElemType, out var isImmArr))
        {
            AddAssertion($"(>= (seq_count_{seqId} {value.Term}) 0)", "CollectionAxiom", $"{value.Type.Name}.Count >= 0", loc);
            EmitSequenceElementAssumptions(value, seqId, seqElemType!, guardCond: null, loc);
            if (isImmArr)
            {
                AddAssertion($"(=> (seq_is_default_{seqId} {value.Term}) (= (seq_count_{seqId} {value.Term}) 0))",
                    "CollectionAxiom", $"{value.Type.Name} default array has zero length", loc);
                if (_nonDefaultImmutableArrays)
                {
                    AddAssertion($"(not (seq_is_default_{seqId} {value.Term}))",
                        "NonDefault", $"{value.Type.Name} is non-default", loc);
                }
            }
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
                var fieldTerm = new SmtTypedTerm($"({f.AccessorName} {value.Term})", f.Type);
                EmitTypeAssumptions(fieldTerm, loc);
                if (f.IsNonDefault && !_nonDefaultImmutableArrays &&
                    _types.TryGetSequenceInfo(f.Type, out _, out var fSeqId, out _, out var fIsImmArr) &&
                    fIsImmArr)
                {
                    AddAssertion($"(not (seq_is_default_{fSeqId} {fieldTerm.Term}))",
                        "NonDefault", $"[NonDefault] field '{value.Type.Name}.{f.Name}'", loc);
                }
            }
        }
    }

    private void EmitCalendarAssumptions(string term, CalendarTypeKind kind, string? guardCond, Location loc)
    {
        void Emit(string formula, string label)
        {
            var f = guardCond is null ? formula : $"(=> {guardCond} {formula})";
            AddAssertion(f, "CalendarAxiom", label, loc);
        }

        switch (kind)
        {
            case CalendarTypeKind.DateOnly:
                Emit($"(and (>= {term} {CalendarArithmetic.DateOnlyMinDayNumber}) (<= {term} {CalendarArithmetic.DateOnlyMaxDayNumber}))",
                    "DateOnly valid range");
                break;
            case CalendarTypeKind.DateTime:
                Emit($"(and (>= {term} {CalendarArithmetic.DateTimeMinTicks}) (<= {term} {CalendarArithmetic.DateTimeMaxTicks}))",
                    "DateTime valid tick range");
                break;
            case CalendarTypeKind.TimeSpan:
                Emit($"(and (>= {term} {CalendarArithmetic.FormatLongInt(CalendarArithmetic.TimeSpanMinTicks)}) (<= {term} {CalendarArithmetic.FormatLongInt(CalendarArithmetic.TimeSpanMaxTicks)}))",
                    "TimeSpan valid tick range");
                break;
            case CalendarTypeKind.DateTimeOffset:
                var utc = $"(dto_utc_ticks {term})";
                var off = $"(dto_offset_ticks {term})";
                Emit($"(and (>= {utc} {CalendarArithmetic.DateTimeMinTicks}) (<= {utc} {CalendarArithmetic.DateTimeMaxTicks}))",
                    "DateTimeOffset UTC tick range");
                Emit($"(and (>= {off} {CalendarArithmetic.FormatLongInt(-CalendarArithmetic.MaxOffsetTicks)}) (<= {off} {CalendarArithmetic.FormatLongInt(CalendarArithmetic.MaxOffsetTicks)}) (= (mod {off} {CalendarArithmetic.TicksPerMinute}) 0))",
                    "DateTimeOffset offset bounds");
                Emit($"(and (>= (+ {utc} {off}) {CalendarArithmetic.DateTimeMinTicks}) (<= (+ {utc} {off}) {CalendarArithmetic.DateTimeMaxTicks}))",
                    "DateTimeOffset local tick range");
                break;
        }
    }

    private void EmitOnDemandElementAssumptions(string guardCond, SmtTypedTerm element, Location loc)
    {
        var key = guardCond + "|" + element.Term;
        if (!_emittedOnDemandKeys.Add(key)) return;

        if (element.Type.TypeKind == TypeKind.Enum &&
            TryGetIntegralBounds(element.Type, out var eMin, out var eMax, out _, out _))
        {
            AddAssertion(
                $"(=> {guardCond} (and (>= {element.Term} {CalendarArithmetic.FormatBigInt(eMin)}) (<= {element.Term} {CalendarArithmetic.FormatBigInt(eMax)})))",
                "EnumRange", $"{element.Type.Name} underlying range", loc);

            if (IsClosedEnum(element.Type, out _, out var closedVals))
            {
                AddAssertion($"(=> {guardCond} {BuildClosedEnumFormula(element.Term, closedVals)})",
                    "ClosedEnum", $"{element.Type.Name} declared members", loc);
            }
            return;
        }

        var calKind = CalendarArithmetic.Classify(element.Type);
        if (calKind != CalendarTypeKind.None)
        {
            EmitCalendarAssumptions(element.Term, calKind, guardCond, loc);
            return;
        }

        if (TryEmitNullableOnDemandAssumptions(guardCond, element, loc)) return;

        if (_types.TryGetSequenceInfo(element.Type, out _, out var elSeqId, out var elElemType, out var elIsImmArr))
        {
            EmitSequenceElementAssumptions(element, elSeqId, elElemType!, guardCond, loc);
        }

        if (_types.TryGetSequenceInfo(element.Type, out _, out elSeqId, out _, out elIsImmArr) && elIsImmArr)
        {
            AddAssertion($"(=> {guardCond} (>= (seq_count_{elSeqId} {element.Term}) 0))",
                "CollectionAxiom", $"{element.Type.Name}.Count >= 0", loc);
            AddAssertion($"(=> {guardCond} (=> (seq_is_default_{elSeqId} {element.Term}) (= (seq_count_{elSeqId} {element.Term}) 0)))",
                "CollectionAxiom", $"{element.Type.Name} default array has zero length", loc);
            if (_nonDefaultImmutableArrays)
            {
                AddAssertion($"(=> {guardCond} (not (seq_is_default_{elSeqId} {element.Term})))",
                    "NonDefault", $"{element.Type.Name} is non-default", loc);
            }
        }

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
                var fieldTerm = new SmtTypedTerm($"({f.AccessorName} {element.Term})", f.Type);
                EmitOnDemandElementAssumptions(guardCond, fieldTerm, loc);
                if (f.IsNonDefault && !_nonDefaultImmutableArrays &&
                    _types.TryGetSequenceInfo(f.Type, out _, out var fSeqId, out _, out var fIsImmArr) &&
                    fIsImmArr)
                {
                    AddAssertion($"(=> {guardCond} (not (seq_is_default_{fSeqId} {fieldTerm.Term})))",
                        "NonDefault", $"[NonDefault] field '{element.Type.Name}.{f.Name}'", loc);
                }
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

            mergedEnv.Vars[sym] = BindSsaValue("phi_" + SmtTypeSystem.SymbolPart(sym.Name), new SmtTypedTerm(iteExpr, type));
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
                    var updated = BindSsaValue("upd_" + SmtTypeSystem.SymbolPart(mr.Member.Name),
                        new SmtTypedTerm($"({comp.ConstructorName} {string.Join(" ", fieldTerms)})", recv.Type));
                    AssignTarget(mr.Instance, updated, env, updateCaptureAlias: false);
                }
                break;
        }
    }

    private static SmtTypedTerm? GetReceiverTerm(IOperation instance, BlockEnv env) => instance switch
    {
        ILocalReferenceOperation lr => env.Vars.TryGetValue(lr.Local, out var v) ? v : null,
        IParameterReferenceOperation pr =>
            env.Vars.TryGetValue(pr.Parameter, out var v) ? v :
            pr.Parameter.Name == CrossAssemblyContractCache.ThisPlaceholder && env.ThisOverride is not null ? env.ThisOverride :
            new SmtTypedTerm("p_" + SmtTypeSystem.SymbolPart(pr.Parameter.Name), pr.Parameter.Type),
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
        if (_inQuantifierDepth > 0)
        {
            if (isFreshFreeSymbol)
            {
                throw new ContractLoweringException($"Unsupported non-pure expression '{prefix}' inside a quantified LINQ predicate.");
            }
            return value;
        }

        var sort = _types.GetSort(value.Type);
        var name = $"ssa_{++_ssaCounter}_{SmtTypeSystem.SymbolPart(prefix)}";
        _decls.Add($"(declare-const {name} {sort})");
        if (!isFreshFreeSymbol)
        {
            AddAssertion($"(= {name} {value.Term})", "SsaBinding", name, Location.None);
        }
        return new SmtTypedTerm(name, value.Type);
    }

    private void EmitSafetyObligation(string kind, string description, Location location, string gamma, string goalTerm, string? reason = null)
    {
        if (_inQuantifierDepth > 0 && _quantifierObligations is not null)
        {
            _quantifierObligations.Add(gamma == "true" ? goalTerm : $"(=> {gamma} {goalTerm})");
            return;
        }

        AddObligation(kind, description, location, gamma, goalTerm, reason);
    }

    internal static bool IsConversionChecked(IConversionOperation conv, Compilation compilation)
    {
        if (conv.IsChecked) return true;
        for (var node = conv.Syntax; node is not null; node = node.Parent)
        {
            if (node is CheckedExpressionSyntax ce)
            {
                return ce.IsKind(SyntaxKind.CheckedExpression);
            }
            if (node is CheckedStatementSyntax cs)
            {
                return cs.IsKind(SyntaxKind.CheckedStatement);
            }
            if (node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax or BaseMethodDeclarationSyntax)
            {
                break;
            }
        }
        return compilation.Options.CheckOverflow;
    }

    private bool TryLowerIntegralOrEnumConversion(
        SmtTypedTerm inner,
        ITypeSymbol target,
        bool isChecked,
        string gamma,
        Location loc,
        out SmtTypedTerm converted)
    {
        converted = default!;
        // Never treat Real (decimal/double/float) as an integral-width destination (preserves enum-to-decimal!).
        if (!TryGetIntegralBounds(inner.Type, out var srcMin, out var srcMax, out _, out _) ||
            !TryGetIntegralBounds(target, out var dstMin, out var dstMax, out var dstBits, out var dstSigned))
        {
            return false;
        }

        var involvesEnum = inner.Type.TypeKind == TypeKind.Enum || target.TypeKind == TypeKind.Enum;
        if (!involvesEnum && !isChecked)
        {
            return false;
        }

        var srcInt = _translator.IntTermForIndex(inner);
        var canOverflow = srcMin < dstMin || srcMax > dstMax;

        if (isChecked && canOverflow)
        {
            var rangeGoal = $"(and (>= {srcInt} {CalendarArithmetic.FormatBigInt(dstMin)}) (<= {srcInt} {CalendarArithmetic.FormatBigInt(dstMax)}))";
            EmitSafetyObligation("Overflow", $"Checked cast to '{target.Name}' must stay within target range", loc, gamma, rangeGoal);
        }

        string resultInt;
        if (!isChecked && canOverflow)
        {
            // Exact two's-complement wrap modulo 2^dstBits
            var modulus = BigInteger.One << dstBits;
            if (!dstSigned)
            {
                resultInt = $"(mod {srcInt} {CalendarArithmetic.FormatBigInt(modulus)})";
            }
            else
            {
                var half = BigInteger.One << (dstBits - 1);
                var halfStr = CalendarArithmetic.FormatBigInt(half);
                var modStr = CalendarArithmetic.FormatBigInt(modulus);
                resultInt = $"(- (mod (+ {srcInt} {halfStr}) {modStr}) {halfStr})";
            }
        }
        else
        {
            resultInt = srcInt;
        }

        var dstSort = _types.GetSort(target);
        var finalTerm = dstSort switch
        {
            "(_ BitVec 32)" => $"((_ int2bv 32) {resultInt})",
            "(_ BitVec 64)" => $"((_ int2bv 64) {resultInt})",
            _ => resultInt
        };
        converted = new SmtTypedTerm(finalTerm, target);
        return true;
    }

    private bool IsClosedEnum(ITypeSymbol type, out INamedTypeSymbol? enumType, out IReadOnlyList<BigInteger> distinctValues)
    {
        enumType = null;
        distinctValues = Array.Empty<BigInteger>();
        if (type is not INamedTypeSymbol nt || nt.TypeKind != TypeKind.Enum)
        {
            return false;
        }

        if (nt.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "System.FlagsAttribute"))
        {
            return false;
        }

        if (!_closedEnums && !VerifiableSymbolHelpers.HasAttribute(nt, VerifiableSymbolHelpers.VerifiableAttr))
        {
            return false;
        }

        var seen = new HashSet<BigInteger>();
        var list = new List<BigInteger>();
        foreach (var f in nt.GetMembers().OfType<IFieldSymbol>())
        {
            if (f.IsConst && f.HasConstantValue && f.ConstantValue is not null)
            {
                var bi = ConvertToBigInteger(f.ConstantValue);
                if (seen.Add(bi))
                {
                    list.Add(bi);
                }
            }
        }

        if (list.Count == 0)
        {
            return false;
        }

        enumType = nt;
        distinctValues = list;
        return true;
    }

    private bool HasAnyClosedEnum(ITypeSymbol type, HashSet<ITypeSymbol>? visited = null)
    {
        visited ??= new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        if (!visited.Add(type)) return false;
        if (IsClosedEnum(type, out _, out _)) return true;
        if (_types.TryGetNullableInfo(type, out _, out _, out var inner) && IsClosedEnum(inner!, out _, out _))
            return true;
        if (type is INamedTypeSymbol nt && !nt.IsGenericType)
        {
            foreach (var p in nt.GetMembers().OfType<IPropertySymbol>())
            {
                if (!p.IsStatic && !p.IsIndexer && p.Name != "EqualityContract" && HasAnyClosedEnum(p.Type, visited))
                    return true;
            }
        }
        return false;
    }

    private static string BuildClosedEnumFormula(string term, IReadOnlyList<BigInteger> values)
    {
        if (values.Count == 1)
        {
            return $"(= {term} {CalendarArithmetic.FormatBigInt(values[0])})";
        }
        return "(or " + string.Join(" ", values.Select(v => $"(= {term} {CalendarArithmetic.FormatBigInt(v)})")) + ")";
    }

    private static bool TryGetIntegralBounds(
        ITypeSymbol type,
        out BigInteger min,
        out BigInteger max,
        out int bitWidth,
        out bool isSigned)
    {
        var effective = type is INamedTypeSymbol { TypeKind: TypeKind.Enum, EnumUnderlyingType: { } u } ? u : type;
        switch (effective.SpecialType)
        {
            case SpecialType.System_SByte:
                min = sbyte.MinValue; max = sbyte.MaxValue; bitWidth = 8; isSigned = true; return true;
            case SpecialType.System_Byte:
                min = byte.MinValue; max = byte.MaxValue; bitWidth = 8; isSigned = false; return true;
            case SpecialType.System_Int16:
                min = short.MinValue; max = short.MaxValue; bitWidth = 16; isSigned = true; return true;
            case SpecialType.System_UInt16:
                min = ushort.MinValue; max = ushort.MaxValue; bitWidth = 16; isSigned = false; return true;
            case SpecialType.System_Char:
                min = char.MinValue; max = char.MaxValue; bitWidth = 16; isSigned = false; return true;
            case SpecialType.System_Int32:
                min = int.MinValue; max = int.MaxValue; bitWidth = 32; isSigned = true; return true;
            case SpecialType.System_UInt32:
                min = uint.MinValue; max = uint.MaxValue; bitWidth = 32; isSigned = false; return true;
            case SpecialType.System_Int64:
                min = long.MinValue; max = long.MaxValue; bitWidth = 64; isSigned = true; return true;
            case SpecialType.System_UInt64:
                min = ulong.MinValue; max = ulong.MaxValue; bitWidth = 64; isSigned = false; return true;
            default:
                min = 0; max = 0; bitWidth = 0; isSigned = false; return false;
        }
    }

    private static BigInteger ConvertToBigInteger(object value) => value switch
    {
        sbyte v => new BigInteger(v),
        byte v => new BigInteger(v),
        short v => new BigInteger(v),
        ushort v => new BigInteger(v),
        char v => new BigInteger(v),
        int v => new BigInteger(v),
        uint v => new BigInteger(v),
        long v => new BigInteger(v),
        ulong v => new BigInteger(v),
        _ => new BigInteger(Convert.ToInt64(value, CultureInfo.InvariantCulture))
    };

    private bool TryLowerRegexIsMatch(
        IInvocationOperation inv,
        string gamma,
        BlockEnv env,
        bool emitObligations,
        int inlineDepth,
        out SmtTypedTerm result)
    {
        result = default!;
        var method = inv.TargetMethod;
        if (method.ContainingType?.OriginalDefinition.ToDisplayString() != "System.Text.RegularExpressions.Regex" ||
            method.Name != "IsMatch")
        {
            return false;
        }

        var loc = inv.Syntax.GetLocation();
        string pattern;
        int options = RegexModel.OptNone;
        IOperation inputOp;

        if (method.IsStatic)
        {
            if (inv.Arguments.Length is < 2 or > 3)
            {
                throw new ContractLoweringException($"Unsupported Regex.IsMatch overload '{inv.Syntax}'.");
            }
            inputOp = GetOrderedArgOp(inv, 0);
            var patStr = ExtractConstantStringArg(GetOrderedArgOp(inv, 1));
            if (patStr is null)
            {
                throw new ContractLoweringException($"Regex.IsMatch pattern must be a compile-time constant string in '{inv.Syntax}'.");
            }
            pattern = patStr;
            if (inv.Arguments.Length == 3)
            {
                if (!TryGetConstantInt(GetOrderedArgOp(inv, 2), out options))
                {
                    throw new ContractLoweringException($"Regex.IsMatch options must be a compile-time constant in '{inv.Syntax}'.");
                }
            }
        }
        else
        {
            if (inv.Instance is null || inv.Arguments.Length != 1)
            {
                throw new ContractLoweringException($"Unsupported Regex.IsMatch overload '{inv.Syntax}'.");
            }
            inputOp = GetOrderedArgOp(inv, 0);
            if (!TryExtractRegexSpecFromInstance(inv.Instance, out pattern, out options, out var instErr))
            {
                throw new ContractLoweringException(instErr ?? $"Unsupported Regex receiver in '{inv.Syntax}'.");
            }
        }

        if (!RegexModel.TryTranslate(pattern, options, out var smtRe, out var reErr))
        {
            throw new ContractLoweringException(reErr!);
        }

        var inputTerm = _translator.UnwrapRefinedIfApplicable(LowerExpression(inputOp, gamma, env, emitObligations, inlineDepth));
        // Always emit path-guarded non-null check in all modes (implementation, spec, query, helper).
        EmitStringNonNullObligation(inputTerm, $"Input must be non-null for Regex.IsMatch in '{inv.Syntax}'", loc, gamma);

        result = new SmtTypedTerm(
            $"(str.in_re (str-text {inputTerm.Term}) {smtRe})",
            _compilation.GetSpecialType(SpecialType.System_Boolean));
        return true;
    }

    private bool TryExtractRegexSpecFromInstance(IOperation instance, out string pattern, out int options, out string? error)
    {
        pattern = string.Empty;
        options = RegexModel.OptNone;
        error = null;

        var cur = instance;
        while (cur is IConversionOperation c) cur = c.Operand;

        if (cur is IInvocationOperation factoryInv &&
            VerifiableSymbolHelpers.TryGetGeneratedRegexAttribute(factoryInv.TargetMethod, out pattern, out options))
        {
            return true;
        }

        if (cur is IPropertyReferenceOperation propRef &&
            VerifiableSymbolHelpers.TryGetGeneratedRegexAttribute(propRef.Property, out pattern, out options))
        {
            return true;
        }

        if (cur is IFieldReferenceOperation { Field: { IsStatic: true, IsReadOnly: true } field })
        {
            // A static constructor (or another static initializer) can replace the Regex the field was initialized with.
            if (StaticFieldMayBeReassigned(field))
            {
                error = $"Static Regex field '{field.Name}' is assigned (or passed by reference) in a static constructor or initializer, so its initializer's pattern is not necessarily the one used.";
                return false;
            }

            foreach (var synRef in field.DeclaringSyntaxReferences)
            {
                var syn = synRef.GetSyntax();
                if (!_compilation.SyntaxTrees.Contains(syn.SyntaxTree)) continue;
                var model = _compilation.GetSemanticModel(syn.SyntaxTree);
                if (syn is VariableDeclaratorSyntax { Initializer.Value: { } initExpr } &&
                    model.GetOperation(initExpr) is IObjectCreationOperation obj &&
                    obj.Type?.OriginalDefinition.ToDisplayString() == "System.Text.RegularExpressions.Regex" &&
                    obj.Arguments.Length is 1 or 2)
                {
                    var pat = ExtractConstantStringArg(obj.Arguments[0].Value);
                    if (pat is null)
                    {
                        error = $"Static Regex field '{field.Name}' pattern must be a compile-time constant string.";
                        return false;
                    }
                    pattern = pat;
                    if (obj.Arguments.Length == 2 && !TryGetConstantInt(obj.Arguments[1].Value, out options))
                    {
                        error = $"Static Regex field '{field.Name}' options must be a compile-time constant.";
                        return false;
                    }
                    return true;
                }
            }
        }

        error = "Instance Regex.IsMatch requires a [GeneratedRegex] method/property or a static readonly Regex field initialized with a constant pattern.";
        return false;
    }

    /// <summary>
    /// True when anything other than the field's own initializer might store a different value into the static
    /// readonly <paramref name="field"/>: any use of it in a static constructor of its type, or a write (assignment,
    /// <c>ref</c>/<c>out</c> argument, address-taking) in another static initializer. A field declared in metadata,
    /// whose code is not visible, counts as reassignable.
    /// </summary>
    private bool StaticFieldMayBeReassigned(IFieldSymbol field)
    {
        var type = field.ContainingType;
        if (type is null || !field.DeclaringSyntaxReferences.Any())
        {
            return true;
        }

        foreach (var syntaxRef in type.DeclaringSyntaxReferences)
        {
            var typeSyntax = syntaxRef.GetSyntax();
            if (!_compilation.SyntaxTrees.Contains(typeSyntax.SyntaxTree))
            {
                return true;
            }

            if (typeSyntax is not TypeDeclarationSyntax typeDecl)
            {
                return true;
            }

            var model = _compilation.GetSemanticModel(typeSyntax.SyntaxTree);
            foreach (var member in typeDecl.Members)
            {
                // Methods cannot assign a readonly field; only the static constructor and initializers can.
                var isStaticCtor = member is ConstructorDeclarationSyntax ctor && ctor.Modifiers.Any(SyntaxKind.StaticKeyword);
                if (!isStaticCtor && member is not (FieldDeclarationSyntax or PropertyDeclarationSyntax))
                {
                    continue;
                }

                foreach (var node in member.DescendantNodes().OfType<ExpressionSyntax>())
                {
                    if (node is not (IdentifierNameSyntax or MemberAccessExpressionSyntax) ||
                        !SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(node).Symbol, field))
                    {
                        continue;
                    }

                    // The name inside "Type.Field" binds to the same symbol; only the outermost expression counts.
                    if (node.Parent is MemberAccessExpressionSyntax parentAccess && parentAccess.Name == node)
                    {
                        continue;
                    }

                    if (isStaticCtor || model.GetOperation(node) is not { } op || !IsPlainRead(op))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool IsPlainRead(IOperation op) => op.Parent switch
    {
        IInvocationOperation inv => ReferenceEquals(inv.Instance, op) || inv.Arguments.Any(a => ReferenceEquals(a.Value, op) && a.Parameter?.RefKind == RefKind.None),
        IPropertyReferenceOperation prop => ReferenceEquals(prop.Instance, op),
        IFieldReferenceOperation fld => ReferenceEquals(fld.Instance, op),
        IArgumentOperation arg => arg.Parameter?.RefKind == RefKind.None,
        _ => false
    };

    private static bool TryExtractImmutableCreateElements(IInvocationOperation inv, out List<IOperation> elements)
    {
        elements = new List<IOperation>();
        if (inv.Arguments.Length == 1 && inv.Arguments[0].Parameter?.IsParams == true)
        {
            return TryExtractBoundedJoinElements(inv.Arguments[0].Value, out elements, out _);
        }

        // Fixed-arity overloads: ImmutableArray.Create(a), Create(a, b), Create(a, b, c), Create(a, b, c, d)
        foreach (var arg in inv.Arguments.OrderBy(a => a.Parameter?.Ordinal ?? 0))
        {
            elements.Add(arg.Value);
        }
        return true;
    }

    private void AddAssertion(string smtFormula, string kind, string label, Location loc, string? reason = null)
    {
        if (_assertionBinders.Count > 0)
        {
            smtFormula = $"(forall ({string.Join(" ", _assertionBinders.Select(b => $"({b} Int)"))}) (! {smtFormula} :pattern ({_assertionPatterns[_assertionPatterns.Count - 1]})))";
        }

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
                            if (ch == '"')
                            {
                                if (i + 1 < limit && s[i + 1] == '"') { i += 2; continue; }
                                inStr = false;
                            }
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

    private void AddObligation(
        string kind,
        string description,
        Location location,
        string gamma,
        string goalTerm,
        string? reason = null,
        Location? contractLocation = null,
        string? contractLocationLabel = null,
        string? resultSmtTerm = null,
        IReadOnlyList<GoalClauseInfo>? goalClauses = null)
    {
        var preamble = string.Join("\n", _types.Declarations.Concat(_decls).Concat(_assertions));
        _obligations.Add(new ProofObligation(
            kind,
            description,
            location,
            gamma,
            goalTerm,
            preamble,
            _trackedParams.ToList(),
            reason,
            contractLocation,
            contractLocationLabel,
            resultSmtTerm,
            goalClauses));
    }

    private IReadOnlyList<GoalClauseInfo>? ExtractGoalClauses(IOperation expr, string gamma, BlockEnv env)
    {
        var conjuncts = new List<IOperation>();
        CollectBooleanConjuncts(expr, conjuncts, maxClauses: 8);
        if (conjuncts.Count == 0)
        {
            return null;
        }

        var result = new List<GoalClauseInfo>(conjuncts.Count);
        foreach (var conj in conjuncts)
        {
            try
            {
                var clauseTerm = LowerExpression(conj, gamma, env, emitObligations: false);
                var clauseText = NormalizeWhitespace(conj.Syntax.ToString());
                string? opStr = null;
                string? leftText = null;
                string? leftTerm = null;
                string? rightText = null;
                string? rightTerm = null;

                var unwrapped = UnwrapParens(conj);
                if (unwrapped is IBinaryOperation bin && TryGetComparisonOperator(bin.OperatorKind, out var op))
                {
                    opStr = op;
                    leftText = NormalizeWhitespace(bin.LeftOperand.Syntax.ToString());
                    rightText = NormalizeWhitespace(bin.RightOperand.Syntax.ToString());
                    try
                    {
                        var l = LowerExpression(bin.LeftOperand, gamma, env, emitObligations: false);
                        var r = LowerExpression(bin.RightOperand, gamma, env, emitObligations: false);
                        leftTerm = l.Term;
                        rightTerm = r.Term;
                    }
                    catch
                    {
                        // Side-value evaluation is best-effort; keep the clause term itself.
                    }
                }

                result.Add(new GoalClauseInfo(clauseText, clauseTerm.Term, opStr, leftText, leftTerm, rightText, rightTerm));
            }
            catch
            {
                // Best-effort clause extraction; never fail lowering because of diagnostic enrichment.
            }
        }

        return result.Count > 0 ? result : null;
    }

    private static void CollectBooleanConjuncts(IOperation op, List<IOperation> output, int maxClauses)
    {
        if (output.Count >= maxClauses) return;
        op = UnwrapParens(op);

        if (op is IBinaryOperation bin &&
            bin.OperatorKind is BinaryOperatorKind.ConditionalAnd or BinaryOperatorKind.And &&
            bin.Type?.SpecialType == SpecialType.System_Boolean)
        {
            CollectBooleanConjuncts(bin.LeftOperand, output, maxClauses);
            CollectBooleanConjuncts(bin.RightOperand, output, maxClauses);
            return;
        }

        output.Add(op);
    }

    private static IOperation UnwrapParens(IOperation op)
    {
        while (op is IParenthesizedOperation paren)
        {
            op = paren.Operand;
        }
        return op;
    }

    private static bool TryGetComparisonOperator(BinaryOperatorKind kind, out string op)
    {
        op = kind switch
        {
            BinaryOperatorKind.Equals => "==",
            BinaryOperatorKind.NotEquals => "!=",
            BinaryOperatorKind.LessThan => "<",
            BinaryOperatorKind.LessThanOrEqual => "<=",
            BinaryOperatorKind.GreaterThan => ">",
            BinaryOperatorKind.GreaterThanOrEqual => ">=",
            _ => string.Empty
        };
        return op.Length > 0;
    }

    private static string NormalizeWhitespace(string text)
    {
        var sb = new StringBuilder(text.Length);
        var prevSpace = false;
        foreach (var ch in text.Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!prevSpace)
                {
                    sb.Append(' ');
                    prevSpace = true;
                }
            }
            else
            {
                sb.Append(ch);
                prevSpace = false;
            }
        }
        return sb.ToString();
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
