using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Verifiable.Core;
using TokenBuilder = System.Collections.Immutable.ImmutableDictionary<Microsoft.CodeAnalysis.ISymbol, Verifiable.Analyzers.TokenState>.Builder;
using TokenMap = System.Collections.Immutable.ImmutableDictionary<Microsoft.CodeAnalysis.ISymbol, Verifiable.Analyzers.TokenState>;

namespace Verifiable.Analyzers;

/// <summary>
/// What <c>VER007</c> knows about one token at one point: the proved kernel state, plus whether the token
/// may already have been consumed on some path that reaches the point.
/// </summary>
/// <remarks>
/// The kernel state (<see cref="ObligationState"/>) only says whether the token is still outstanding, and it
/// cannot tell "consumed on one path" apart from "never created on that path". The extra flag is the
/// analyzer's own, not proved: it is set by every consumption, cleared when the variable is given a new
/// token, and merged with "or". A second consumption while it is set is reported as a possible double
/// consumption (a consume after a partial branch, in a loop, or of a sink method's own parameter).
/// </remarks>
internal readonly record struct TokenState(ObligationState Kernel, bool MaybeConsumed);

/// <summary>
/// Path-sensitive CFG dataflow analyzer enforcing linear obligation tokens (<c>[MustConsume]</c> / <c>[ConsumesObligation]</c>)
/// across all methods in the compilation (VER007).
/// </summary>
/// <remarks>
/// <para>
/// Finally blocks run once per way of reaching them (normal completion, each <c>return</c>/<c>break</c> that leaves
/// the try, and an exception), so each way has its own state and the finally's effect is not smeared across them.
/// A catch block (and an exceptional finally) starts from the join of the states at each point in the try body
/// where an exception could be thrown, minus the locals that go out of scope. A token passed to a sink is
/// consumed even if the sink then throws.
/// </para>
/// <para>
/// Tokens must travel in locals and parameters. Passing one to a method that is not <c>[ConsumesObligation]</c>,
/// or storing it in an array, tuple, <c>Nullable&lt;T&gt;</c>, field or collection, is rejected, because the analyzer
/// cannot follow the copy (the token is treated as moved, so the rejection is reported once).
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class LinearObligationAnalyzer : DiagnosticAnalyzer
{
    private sealed record LinearDiag(
        Location Loc,
        Location? RelatedLoc,
        string? RelatedHeading,
        string Title,
        string TokenName,
        string TypeName,
        string Detail,
        string FixHint);

    /// <summary>A local function's effect on the tokens it captures: their state after it returns.</summary>
    private sealed class LocalSummary
    {
        public static readonly LocalSummary Empty = new(new Dictionary<ISymbol, TokenState>(SymbolEqualityComparer.Default));

        public LocalSummary(Dictionary<ISymbol, TokenState> exit) => Exit = exit;

        public Dictionary<ISymbol, TokenState> Exit { get; }
    }

    /// <summary>A finally block being run on behalf of a branch that leaves its try (or of an exception).</summary>
    private sealed class FinallyFrame
    {
        public FinallyFrame(ControlFlowRegion region, ControlFlowBranch? branch, int nextFinally, FinallyFrame? outer)
        {
            Region = region;
            Branch = branch;
            NextFinally = nextFinally;
            Outer = outer;
        }

        public ControlFlowRegion Region { get; }

        /// <summary>The branch to carry on with once the finally ends, or null when the finally runs for an exception.</summary>
        public ControlFlowBranch? Branch { get; }

        public int NextFinally { get; }

        public FinallyFrame? Outer { get; }
    }

    private readonly record struct Node(BasicBlock Block, FinallyFrame? Ctx);

    private sealed class HandlerInfo
    {
        public HandlerInfo(BasicBlock entry, ControlFlowRegion? finallyRegion, HashSet<ISymbol> hidden)
        {
            Entry = entry;
            FinallyRegion = finallyRegion;
            Hidden = hidden;
        }

        public BasicBlock Entry { get; }

        public ControlFlowRegion? FinallyRegion { get; }

        /// <summary>The locals of the try body, which are out of scope in the handler.</summary>
        public HashSet<ISymbol> Hidden { get; }
    }

    private sealed class CaptureAlias
    {
        public List<ISymbol> Symbols { get; } = [];

        /// <summary>True when the capture can also hold something that is not a tracked token variable.</summary>
        public bool HasOther { get; set; }
    }

    /// <summary>The token variables an expression stands for. More than one means "one of these".</summary>
    private readonly struct TokenRef
    {
        public TokenRef(IReadOnlyList<ISymbol> symbols, bool exact)
        {
            Symbols = symbols;
            Exact = exact;
        }

        public IReadOnlyList<ISymbol> Symbols { get; }

        public bool Exact { get; }
    }

    private enum ConsumeContext
    {
        Argument,
        Assignment,
        Return,
        Other
    }

    /// <summary>One run of the analyzer over a method body: its local functions' summaries and what it has reported.</summary>
    private sealed class Session
    {
        public Session(OperationBlockAnalysisContext context)
        {
            Context = context;
        }

        public OperationBlockAnalysisContext Context { get; }

        public HashSet<(int Start, int Length, string Message)> Reported { get; } = [];

        public Dictionary<IMethodSymbol, ControlFlowGraph> LocalCfgs { get; } = new(SymbolEqualityComparer.Default);

        public Dictionary<IMethodSymbol, LocalSummary> Summaries { get; } = new(SymbolEqualityComparer.Default);

        public HashSet<IMethodSymbol> InProgress { get; } = new(SymbolEqualityComparer.Default);

        public LocalSummary GetSummary(IMethodSymbol localFunction)
        {
            var key = localFunction.OriginalDefinition;
            if (Summaries.TryGetValue(key, out var done))
            {
                return done;
            }

            if (!LocalCfgs.TryGetValue(key, out var cfg) || !InProgress.Add(key))
            {
                return LocalSummary.Empty;
            }

            try
            {
                var summary = AnalyzeCfgTree(this, key, cfg, isLocalFunction: true) ?? LocalSummary.Empty;
                Summaries[key] = summary;
                return summary;
            }
            finally
            {
                InProgress.Remove(key);
            }
        }
    }

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        [VerifiableDescriptors.LinearObligationViolation];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterOperationBlockAction(AnalyzeOperationBlock);
        context.RegisterOperationAction(AnalyzeMemberInitializer, OperationKind.FieldInitializer, OperationKind.PropertyInitializer);
    }

    /// <summary>
    /// A field or property initializer (<c>static readonly T Shared = new(1);</c>, <c>T Tok { get; } = new(1);</c>) that
    /// holds a token puts it where the analyzer cannot follow it, just like an assignment to a field.
    /// </summary>
    private static void AnalyzeMemberInitializer(OperationAnalysisContext context)
    {
        var initializer = (ISymbolInitializerOperation)context.Operation;
        var value = initializer.Value;
        var holdsToken = VerifiableSymbolHelpers.ContainsMustConsumeElement(UnwrapConversion(value).Type, out var linearType);
        if (!holdsToken)
        {
            foreach (var member in GetInitializedMembers(initializer))
            {
                if (VerifiableSymbolHelpers.ContainsMustConsumeElement(GetSymbolTypeOrNull(member), out linearType))
                {
                    holdsToken = true;
                    break;
                }
            }
        }

        var first = GetInitializedMembers(initializer).FirstOrDefault();
        if (!holdsToken || first is null || VerifiableSymbolHelpers.IsGeneratedByVerifiableGenerators(first))
        {
            return;
        }

        var name = first.Name;
        var elm = ElmDiagnosticFormatter.Format(
            "UNTRACKED OBLIGATION",
            value.Syntax.GetLocation(),
            $"Linear obligation token `{name}` (`{linearType!.Name}`) was stored in a field or property initializer, where the analyzer cannot follow it.",
            spirit: "A `[MustConsume]` token represents work that must finish exactly once along every code path (such as emitting a command or committing state).",
            fixHint: "Keep the token in a local variable or parameter and pass it to a `[ConsumesObligation]` method, return it, or call `Obligation.Discard(...)`. Do not store tokens in arrays, tuples, `Nullable<T>`, fields or collections.",
            relatedLocation: null,
            relatedHeading: null);
        context.ReportDiagnostic(Diagnostic.Create(VerifiableDescriptors.LinearObligationViolation, value.Syntax.GetLocation(), elm));
    }

    private static IEnumerable<ISymbol> GetInitializedMembers(ISymbolInitializerOperation initializer) => initializer switch
    {
        IFieldInitializerOperation f => f.InitializedFields,
        IPropertyInitializerOperation p => p.InitializedProperties,
        _ => []
    };

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

        var session = new Session(context);

        foreach (var root in context.OperationBlocks)
        {
            if (root is IBlockOperation or IMethodBodyOperation or IConstructorBodyOperation)
            {
                var cfg = context.GetControlFlowGraph(root);
                AnalyzeCfgTree(session, owning, cfg, isLocalFunction: false);
            }
        }
    }

    /// <summary>
    /// Analyzes one CFG, then the local functions and anonymous functions inside it. Returns the summary of the
    /// CFG when it is a local function's.
    /// </summary>
    private static LocalSummary? AnalyzeCfgTree(Session session, ISymbol methodSymbol, ControlFlowGraph cfg, bool isLocalFunction)
    {
        foreach (var localFn in cfg.LocalFunctions)
        {
            session.LocalCfgs[localFn.OriginalDefinition] = cfg.GetLocalFunctionControlFlowGraph(localFn);
        }

        var summary = new Analysis(session, methodSymbol, cfg, isLocalFunction).Run();

        foreach (var localFn in cfg.LocalFunctions)
        {
            session.GetSummary(localFn);
        }

        foreach (var block in cfg.Blocks)
        {
            foreach (var op in block.Operations)
            {
                foreach (var flowAnon in op.DescendantsAndSelf().OfType<IFlowAnonymousFunctionOperation>())
                {
                    var anonCfg = cfg.GetAnonymousFunctionControlFlowGraph(flowAnon);
                    AnalyzeCfgTree(session, flowAnon.Symbol, anonCfg, isLocalFunction: false);
                }
            }

            if (block.BranchValue is { } bv)
            {
                foreach (var flowAnon in bv.DescendantsAndSelf().OfType<IFlowAnonymousFunctionOperation>())
                {
                    var anonCfg = cfg.GetAnonymousFunctionControlFlowGraph(flowAnon);
                    AnalyzeCfgTree(session, flowAnon.Symbol, anonCfg, isLocalFunction: false);
                }
            }
        }

        return summary;
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

    // -----------------------------------------------------------------------------------------------
    // Shared helpers
    // -----------------------------------------------------------------------------------------------

    private static IOperation UnwrapConversion(IOperation op)
    {
        while (op is IConversionOperation conv)
        {
            op = conv.Operand;
        }
        return op;
    }

    private static ITypeSymbol? GetSymbolTypeOrNull(ISymbol symbol) => symbol switch
    {
        ILocalSymbol l => l.Type,
        IParameterSymbol p => p.Type,
        _ => null
    };

    private static ITypeSymbol GetSymbolType(ISymbol symbol) =>
        GetSymbolTypeOrNull(symbol) ?? throw new InvalidOperationException($"Unexpected symbol kind {symbol.Kind}");

    private static string GetTypeName(ISymbol symbol) => GetSymbolType(symbol).Name;

    private static bool IsToken(ITypeSymbol? type) => VerifiableSymbolHelpers.IsMustConsumeType(type);

    /// <summary>A type that can hold a token without being one: a tuple or a <c>Nullable&lt;T&gt;</c> of a token.</summary>
    private static bool HoldsTokenIndirectly(ITypeSymbol? type) =>
        type is not null && !IsToken(type) && VerifiableSymbolHelpers.ContainsMustConsumeElement(type, out _);

    private static bool IsAwaitable(ITypeSymbol? type) =>
        type is INamedTypeSymbol named &&
        (named.Name == "Task" || named.Name == "ValueTask") &&
        named.ContainingNamespace?.ToDisplayString() == "System.Threading.Tasks";

    private static bool IsNormalReturnOrExit(ControlFlowBranch? branch) =>
        branch is not null &&
        (branch.Semantics == ControlFlowBranchSemantics.Return ||
         (branch.Destination?.Kind == BasicBlockKind.Exit &&
          branch.Semantics == ControlFlowBranchSemantics.Regular));

    private static bool RegionContains(ControlFlowRegion region, BasicBlock block) =>
        region.FirstBlockOrdinal <= block.Ordinal && block.Ordinal <= region.LastBlockOrdinal;

    private static TokenMap Join(TokenMap a, TokenMap b)
    {
        var result = ImmutableDictionary.CreateBuilder<ISymbol, TokenState>(SymbolEqualityComparer.Default);
        var keys = new HashSet<ISymbol>(a.Keys, SymbolEqualityComparer.Default);
        keys.UnionWith(b.Keys);

        foreach (var key in keys)
        {
            var hasA = a.TryGetValue(key, out var stA);
            var hasB = b.TryGetValue(key, out var stB);

            // The per-token merge rule is the proved kernel lattice (src/Verifiable.Core/ObligationLattice.cs).
            // The "may already be consumed" flag is merged with "or".
            result[key] = hasA && hasB
                ? new TokenState(ObligationLattice.Join(stA.Kernel, stB.Kernel), stA.MaybeConsumed || stB.MaybeConsumed)
                : new TokenState(
                    ObligationLattice.JoinWithUntracked((hasA ? stA : stB).Kernel),
                    (hasA ? stA : stB).MaybeConsumed);
        }

        return result.ToImmutable();
    }

    private static bool DictionariesEqual(TokenMap a, TokenMap b)
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

    // -----------------------------------------------------------------------------------------------
    // The dataflow over one CFG
    // -----------------------------------------------------------------------------------------------

    private sealed class Analysis
    {
        private readonly Session session;
        private readonly ISymbol methodSymbol;
        private readonly ControlFlowGraph cfg;
        private readonly bool isLocalFunction;
        private readonly Dictionary<CaptureId, CaptureAlias> captures = [];
        private readonly HashSet<ISymbol> capturedTokens = new(SymbolEqualityComparer.Default);
        private readonly Dictionary<ControlFlowRegion, HashSet<ISymbol>> localsCache = [];
        private readonly Dictionary<BasicBlock, List<HandlerInfo>> handlerCache = [];
        private readonly Dictionary<(ControlFlowRegion, ControlFlowBranch?, int, FinallyFrame?), FinallyFrame> frames = [];

        private readonly Dictionary<Node, TokenMap> inStates = [];
        private readonly List<Node> order = [];

        public Analysis(Session session, ISymbol methodSymbol, ControlFlowGraph cfg, bool isLocalFunction)
        {
            this.session = session;
            this.methodSymbol = methodSymbol;
            this.cfg = cfg;
            this.isLocalFunction = isLocalFunction;
        }

        public Session Session => session;

        public Dictionary<ISymbol, Location> ConsumedSites { get; } = new(SymbolEqualityComparer.Default);

        public bool TryGetCapture(CaptureId id, out CaptureAlias alias) => captures.TryGetValue(id, out alias!);

        public LocalSummary? Run()
        {
            if (cfg.Blocks.Length == 0)
            {
                return null;
            }

            var isSinkMethod = VerifiableSymbolHelpers.HasAttribute(methodSymbol, VerifiableSymbolHelpers.ConsumesObligationAttr);
            var entryState = ImmutableDictionary.CreateBuilder<ISymbol, TokenState>(SymbolEqualityComparer.Default);
            var ownSymbols = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            if (methodSymbol is IMethodSymbol ms)
            {
                foreach (var p in ms.Parameters)
                {
                    ownSymbols.Add(p);
                    // An out parameter holds no token on entry: the caller gets whatever the method assigns to it.
                    if (IsToken(p.Type) && p.RefKind != RefKind.Out)
                    {
                        entryState[p] = new TokenState(LinearTransfer.Entry(isSinkMethod), false);
                    }
                }
            }

            BuildCaptureAliases();

            if (isLocalFunction)
            {
                FindCapturedTokens(ownSymbols);
                foreach (var captured in capturedTokens)
                {
                    // The local function does not own the token: the state it leaves behind becomes its summary,
                    // and a token it leaves untouched is not a leak.
                    entryState[captured] = new TokenState(ObligationState.Live, false);
                }
            }

            var entryNode = new Node(cfg.Blocks[0], null);
            inStates[entryNode] = entryState.ToImmutable();
            order.Add(entryNode);

            var worklist = new Queue<Node>();
            worklist.Enqueue(entryNode);

            // Phase 1: Fixed-point dataflow convergence.
            // This always finishes, so it has no fixed cap (a cap could stop a large method early and silently
            // drop its diagnostics). An incoming state only rises (LatticeLaws.JoinIsAnUpperBound), and each token
            // in it appears once and then rises at most twice (TransferLaws.JoinThatChangesAStateRaisesItsRank and
            // RankIsBounded), and its "may be consumed" flag rises at most once. So a node (a block, run for one
            // way of reaching it) is queued at most 1 + 4 * tokens times. Going past that bound means this
            // argument is wrong, which is reported as an analyzer failure rather than a silent pass.
            var trackedTokens = new HashSet<ISymbol>(entryState.Keys, SymbolEqualityComparer.Default);
            long visits = 0;
            while (worklist.Count > 0)
            {
                if (++visits > 1 + (long)inStates.Count * (1 + 4L * trackedTokens.Count))
                {
                    throw new InvalidOperationException("The VER007 analysis did not converge within its proved bound.");
                }

                var node = worklist.Dequeue();
                var inState = inStates[node];

                var handlers = HandlersFor(node.Block);
                var snapshots = handlers.Count > 0 ? new List<TokenMap>() : null;
                var outState = SimulateBlock(node.Block, inState, diagnostics: null, snapshots);

                var targets = new List<Node>();
                CollectSuccessors(node, targets, completions: null);
                foreach (var target in targets)
                {
                    Propagate(target, outState, worklist, trackedTokens);
                }

                if (snapshots is { Count: > 0 })
                {
                    foreach (var handler in handlers)
                    {
                        var joined = JoinForHandler(snapshots, handler);
                        var handlerNode = handler.FinallyRegion is { } fin
                            ? new Node(handler.Entry, Intern(fin, null, 0, Normalize(node.Ctx, handler.Entry)))
                            : new Node(handler.Entry, Normalize(node.Ctx, handler.Entry));
                        Propagate(handlerNode, joined, worklist, trackedTokens);
                    }
                }
            }

            // Phase 2: Final deterministic diagnostic emission on converged states
            var completionStates = new List<TokenMap>();
            foreach (var node in order.OrderBy(n => n.Block.Ordinal).ToList())
            {
                if (node.Block.Kind == BasicBlockKind.Exit)
                {
                    continue;
                }

                var diags = new List<LinearDiag>();
                var outState = SimulateBlock(node.Block, inStates[node], diags, snapshots: null);

                var completions = new List<ControlFlowBranch>();
                CollectSuccessors(node, targets: null, completions);
                foreach (var branch in completions)
                {
                    CheckReturnBranchLeaks(branch, outState, diags);
                    completionStates.Add(outState);
                }

                foreach (var d in diags)
                {
                    Emit(d);
                }
            }

            if (!isLocalFunction)
            {
                return null;
            }

            var exit = new Dictionary<ISymbol, TokenState>(SymbolEqualityComparer.Default);
            foreach (var captured in capturedTokens)
            {
                TokenState? merged = null;
                foreach (var st in completionStates)
                {
                    if (!st.TryGetValue(captured, out var one))
                    {
                        continue;
                    }

                    merged = merged is null
                        ? one
                        : new TokenState(ObligationLattice.Join(merged.Value.Kernel, one.Kernel), merged.Value.MaybeConsumed || one.MaybeConsumed);
                }

                if (merged is { } m)
                {
                    exit[captured] = m;
                }
            }

            return new LocalSummary(exit);
        }

        private void Propagate(Node target, TokenMap state, Queue<Node> worklist, HashSet<ISymbol> trackedTokens)
        {
            var hasOld = inStates.TryGetValue(target, out var oldIn);
            var merged = hasOld ? Join(oldIn!, state) : state;
            if (!hasOld || !DictionariesEqual(oldIn!, merged))
            {
                if (!hasOld)
                {
                    order.Add(target);
                }

                inStates[target] = merged;
                trackedTokens.UnionWith(merged.Keys);
                worklist.Enqueue(target);
            }
        }

        private void Emit(LinearDiag d)
        {
            var span = d.Loc.SourceSpan;
            if (!session.Reported.Add((span.Start, span.Length, d.Detail)))
            {
                return;
            }

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
            session.Context.ReportDiagnostic(Diagnostic.Create(
                VerifiableDescriptors.LinearObligationViolation,
                d.Loc,
                additionalLocations: additionalLocs,
                elmMessage));
        }

        // -------------------------------------------------------------------------------------------
        // Capture aliases and captured tokens
        // -------------------------------------------------------------------------------------------

        /// <summary>
        /// Roslyn spills an argument into a flow capture when a later argument branches (<c>F(p, c ? x : y)</c>) and
        /// when a conditional is itself an argument (<c>F(c ? a : b)</c>). Record which token variables each capture
        /// can hold, so that consuming the capture consumes them.
        /// </summary>
        private void BuildCaptureAliases()
        {
            foreach (var block in cfg.Blocks)
            {
                foreach (var op in block.Operations)
                {
                    foreach (var capture in op.DescendantsAndSelf().OfType<IFlowCaptureOperation>())
                    {
                        if (!IsToken(capture.Value.Type))
                        {
                            continue;
                        }

                        if (!captures.TryGetValue(capture.Id, out var alias))
                        {
                            alias = new CaptureAlias();
                            captures[capture.Id] = alias;
                        }

                        var value = UnwrapConversion(capture.Value);
                        switch (value)
                        {
                            case ILocalReferenceOperation lr:
                                alias.Symbols.Add(lr.Local);
                                break;
                            case IParameterReferenceOperation pr:
                                alias.Symbols.Add(pr.Parameter);
                                break;
                            case IFlowCaptureReferenceOperation inner when captures.TryGetValue(inner.Id, out var innerAlias):
                                alias.Symbols.AddRange(innerAlias.Symbols);
                                alias.HasOther |= innerAlias.HasOther;
                                break;
                            default:
                                alias.HasOther = true;
                                break;
                        }
                    }
                }
            }
        }

        /// <summary>The tokens a local function uses but does not declare: it borrows them from the method around it.</summary>
        private void FindCapturedTokens(HashSet<ISymbol> ownParameters)
        {
            var declared = new HashSet<ISymbol>(ownParameters, SymbolEqualityComparer.Default);
            CollectLocals(cfg.Root, declared);

            foreach (var block in cfg.Blocks)
            {
                IEnumerable<IOperation> ops = block.BranchValue is { } bv ? block.Operations.Append(bv) : block.Operations;
                foreach (var top in ops)
                {
                    foreach (var op in top.DescendantsAndSelf())
                    {
                        switch (op)
                        {
                            case ILocalReferenceOperation lr when IsToken(lr.Local.Type) && !declared.Contains(lr.Local):
                                capturedTokens.Add(lr.Local);
                                break;
                            case IParameterReferenceOperation pr when IsToken(pr.Parameter.Type) && !declared.Contains(pr.Parameter):
                                capturedTokens.Add(pr.Parameter);
                                break;
                            case IInvocationOperation { TargetMethod.MethodKind: MethodKind.LocalFunction } call:
                                // Calling another local function uses the tokens it captures.
                                foreach (var sym in session.GetSummary(call.TargetMethod).Exit.Keys)
                                {
                                    if (!declared.Contains(sym))
                                    {
                                        capturedTokens.Add(sym);
                                    }
                                }
                                break;
                        }
                    }
                }
            }
        }

        private static void CollectLocals(ControlFlowRegion region, HashSet<ISymbol> into)
        {
            foreach (var local in region.Locals)
            {
                into.Add(local);
            }

            foreach (var nested in region.NestedRegions)
            {
                CollectLocals(nested, into);
            }
        }

        // -------------------------------------------------------------------------------------------
        // Control flow: successors, finally blocks and exception handlers
        // -------------------------------------------------------------------------------------------

        private FinallyFrame Intern(ControlFlowRegion region, ControlFlowBranch? branch, int nextFinally, FinallyFrame? outer)
        {
            var key = (region, branch, nextFinally, outer);
            if (!frames.TryGetValue(key, out var frame))
            {
                frame = new FinallyFrame(region, branch, nextFinally, outer);
                frames[key] = frame;
            }

            return frame;
        }

        /// <summary>Drops the finally frames that do not contain the destination (they have finished running).</summary>
        private static FinallyFrame? Normalize(FinallyFrame? ctx, BasicBlock destination)
        {
            while (ctx is not null && !RegionContains(ctx.Region, destination))
            {
                ctx = ctx.Outer;
            }

            return ctx;
        }

        /// <summary>
        /// Lists where control can go after a block: the blocks it flows to, and the return/exit branches that
        /// complete there (after any finally blocks on the way have run).
        /// </summary>
        private void CollectSuccessors(Node node, List<Node>? targets, List<ControlFlowBranch>? completions)
        {
            var block = node.Block;
            foreach (var branch in new[] { block.FallThroughSuccessor, block.ConditionalSuccessor })
            {
                if (branch is null)
                {
                    continue;
                }

                if (branch.Destination is null)
                {
                    // The end of a finally block: carry on with whatever made it run. (An exceptional finally
                    // has nowhere to carry on to: the exception goes on to the outer handlers.)
                    if (branch.Semantics == ControlFlowBranchSemantics.StructuredExceptionHandling &&
                        node.Ctx is { } frame &&
                        RegionContains(frame.Region, block) &&
                        frame.Branch is not null)
                    {
                        Leave(frame.Branch, frame.NextFinally, frame.Outer, targets, completions);
                    }

                    continue;
                }

                Leave(branch, 0, node.Ctx, targets, completions);
            }
        }

        private void Leave(
            ControlFlowBranch branch,
            int finallyIndex,
            FinallyFrame? ctx,
            List<Node>? targets,
            List<ControlFlowBranch>? completions)
        {
            if (finallyIndex < branch.FinallyRegions.Length)
            {
                var region = branch.FinallyRegions[finallyIndex];
                var entry = cfg.Blocks[region.FirstBlockOrdinal];
                targets?.Add(new Node(entry, Intern(region, branch, finallyIndex + 1, ctx)));
                return;
            }

            var destination = branch.Destination!;
            if (IsNormalReturnOrExit(branch))
            {
                completions?.Add(branch);
            }

            targets?.Add(new Node(destination, Normalize(ctx, destination)));
        }

        private List<HandlerInfo> HandlersFor(BasicBlock block)
        {
            if (handlerCache.TryGetValue(block, out var cached))
            {
                return cached;
            }

            var handlers = new List<HandlerInfo>();
            for (var region = block.EnclosingRegion; region is not null; region = region.EnclosingRegion)
            {
                if (region.Kind != ControlFlowRegionKind.Try || region.EnclosingRegion is not { } parent)
                {
                    continue;
                }

                if (parent.Kind == ControlFlowRegionKind.TryAndCatch)
                {
                    var hidden = LocalsInSubtree(region);
                    foreach (var nested in parent.NestedRegions)
                    {
                        if (nested.Kind == ControlFlowRegionKind.Catch)
                        {
                            handlers.Add(new HandlerInfo(cfg.Blocks[nested.FirstBlockOrdinal], null, hidden));
                        }
                        else if (nested.Kind == ControlFlowRegionKind.FilterAndHandler)
                        {
                            foreach (var part in nested.NestedRegions)
                            {
                                handlers.Add(new HandlerInfo(cfg.Blocks[part.FirstBlockOrdinal], null, hidden));
                            }
                        }
                    }
                }
                else if (parent.Kind == ControlFlowRegionKind.TryAndFinally)
                {
                    var hidden = LocalsInSubtree(region);
                    foreach (var nested in parent.NestedRegions)
                    {
                        if (nested.Kind == ControlFlowRegionKind.Finally)
                        {
                            handlers.Add(new HandlerInfo(cfg.Blocks[nested.FirstBlockOrdinal], nested, hidden));
                        }
                    }
                }
            }

            handlerCache[block] = handlers;
            return handlers;
        }

        private HashSet<ISymbol> LocalsInSubtree(ControlFlowRegion region)
        {
            if (!localsCache.TryGetValue(region, out var set))
            {
                set = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
                CollectLocals(region, set);
                localsCache[region] = set;
            }

            return set;
        }

        /// <summary>
        /// The state a handler starts from: the join of the states at each point in the try body where an exception
        /// could be thrown, without the try body's own locals.
        /// </summary>
        private static TokenMap JoinForHandler(List<TokenMap> snapshots, HandlerInfo handler)
        {
            TokenMap? joined = null;
            foreach (var snapshot in snapshots)
            {
                var visible = snapshot;
                foreach (var hidden in handler.Hidden)
                {
                    if (visible.ContainsKey(hidden))
                    {
                        visible = visible.Remove(hidden);
                    }
                }

                joined = joined is null ? visible : Join(joined, visible);
            }

            return joined!;
        }

        // -------------------------------------------------------------------------------------------
        // One block
        // -------------------------------------------------------------------------------------------

        private TokenMap SimulateBlock(BasicBlock block, TokenMap state, List<LinearDiag>? diagnostics, List<TokenMap>? snapshots)
        {
            var walker = new Walker(this, state.ToBuilder(), diagnostics, snapshots);
            walker.Run(block);
            return walker.Result;
        }

        private void CheckReturnBranchLeaks(ControlFlowBranch branch, TokenMap postState, List<LinearDiag> diagnostics)
        {
            var block = branch.Source;
            var returnLoc = block.Operations.LastOrDefault()?.Syntax.GetLocation()
                ?? block.BranchValue?.Syntax.GetLocation()
                ?? Location.None;

            foreach (var kv in postState)
            {
                // An out parameter hands its token to the caller, and a local function leaves captured tokens to the
                // method around it.
                if (kv.Key is IParameterSymbol { RefKind: RefKind.Out } || capturedTokens.Contains(kv.Key))
                {
                    continue;
                }

                var exitEvent = LinearTransfer.ExitEvent(kv.Value.Kernel);
                if (exitEvent != TransferEvent.Ok)
                {
                    var isPartialBranch = exitEvent == TransferEvent.PartialLeak;
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
    }

    // -----------------------------------------------------------------------------------------------
    // The statement walk
    // -----------------------------------------------------------------------------------------------

    private sealed class Walker
    {
        private readonly Analysis analysis;
        private readonly TokenBuilder state;
        private readonly List<LinearDiag>? diagnostics;
        private readonly List<TokenMap>? snapshots;
        private readonly HashSet<IOperation> consumedExpressions = [];
        private readonly HashSet<IOperation> returnOwned = [];
        private bool snapshotTaken;

        public Walker(Analysis analysis, TokenBuilder state, List<LinearDiag>? diagnostics, List<TokenMap>? snapshots)
        {
            this.analysis = analysis;
            this.state = state;
            this.diagnostics = diagnostics;
            this.snapshots = snapshots;
        }

        public TokenMap Result => state.ToImmutable();

        public void Run(BasicBlock block)
        {
            foreach (var op in block.Operations)
            {
                snapshotTaken = false;
                ProcessTopLevelOperation(op);
                if (!snapshotTaken)
                {
                    Snapshot();
                }
            }

            if (block.BranchValue is { } branchVal)
            {
                var isReturnBranch = block.FallThroughSuccessor?.Semantics == ControlFlowBranchSemantics.Return
                    && block.ConditionalSuccessor is null;
                if (isReturnBranch)
                {
                    MarkReturnedOwnership(branchVal);
                }
                Walk(branchVal);
                Snapshot();
            }
        }

        /// <summary>
        /// Records the state at a point where an exception could be thrown, for the catch and finally blocks of the
        /// try the block is in. A sink owns its token from the call on, so the state is taken after the arguments
        /// have been consumed and before the result is assigned.
        /// </summary>
        private void Snapshot()
        {
            snapshots?.Add(state.ToImmutable());
            snapshotTaken = true;
        }

        private void Report(
            Location loc,
            Location? related,
            string? relatedHeading,
            string title,
            string tokenName,
            string typeName,
            string detail,
            string fixHint) =>
            diagnostics?.Add(new LinearDiag(loc, related, relatedHeading, title, tokenName, typeName, detail, fixHint));

        // -------------------------------------------------------------------------------------------
        // Top-level operations
        // -------------------------------------------------------------------------------------------

        private void ProcessTopLevelOperation(IOperation op)
        {
            switch (op)
            {
                case IExpressionStatementOperation exprStmt:
                    ProcessExpressionStatement(exprStmt.Operation);
                    break;

                case IVariableDeclarationGroupOperation declGroup:
                    foreach (var decl in declGroup.Declarations)
                    {
                        foreach (var declarator in decl.Declarators)
                        {
                            HandleVariableDeclarator(declarator);
                        }
                    }
                    break;

                case IVariableDeclarationOperation singleDecl:
                    foreach (var declarator in singleDecl.Declarators)
                    {
                        HandleVariableDeclarator(declarator);
                    }
                    break;

                case IVariableDeclaratorOperation directDeclarator:
                    HandleVariableDeclarator(directDeclarator);
                    break;

                case ISimpleAssignmentOperation simpleAssign:
                    HandleSimpleAssignment(simpleAssign);
                    break;

                case IDeconstructionAssignmentOperation deconstructAssign:
                    HandleDeconstructionAssignment(deconstructAssign);
                    break;

                case IReturnOperation retOp:
                    if (retOp.ReturnedValue is { } retVal)
                    {
                        MarkReturnedOwnership(retVal);
                        Walk(retVal);
                    }
                    break;

                default:
                    Walk(op);
                    break;
            }
        }

        private void ProcessExpressionStatement(IOperation expr)
        {
            Walk(expr);

            if (expr.IsImplicit)
            {
                return;
            }

            var unwrapped = UnwrapConversion(expr);

            // Assignments and deconstructions account for the tokens they move or store themselves.
            if (unwrapped is ISimpleAssignmentOperation or IDeconstructionAssignmentOperation)
            {
                return;
            }

            if (!consumedExpressions.Contains(unwrapped) &&
                VerifiableSymbolHelpers.ContainsMustConsumeElement(unwrapped.Type, out var linearType))
            {
                if (unwrapped is not ILocalReferenceOperation && unwrapped is not IParameterReferenceOperation)
                {
                    Report(
                        expr.Syntax.GetLocation(),
                        null,
                        null,
                        "DROPPED OBLIGATION",
                        "<temporary>",
                        linearType!.Name,
                        "was produced and immediately dropped without being stored or consumed",
                        "Bind the returned obligation to a local variable (`var token = ...;`) and pass it to a `[ConsumesObligation]` method, return it, or call `Obligation.Discard(...)`.");
                }
            }
        }

        // -------------------------------------------------------------------------------------------
        // Assignments
        // -------------------------------------------------------------------------------------------

        private void HandleVariableDeclarator(IVariableDeclaratorOperation declarator)
        {
            var initVal = declarator.Initializer?.Value;
            if (initVal is not null)
            {
                Walk(initVal, skipMovedRoot: true);
            }

            if (IsToken(declarator.Symbol.Type))
            {
                if (initVal is not null)
                {
                    AssignToLinearTarget(declarator.Symbol, initVal, declarator.Syntax.GetLocation());
                }
            }
            else if (initVal is not null && HoldsTokenIndirectly(declarator.Symbol.Type))
            {
                Escape(initVal, declarator.Syntax.GetLocation(), $"was stored in a variable of type `{declarator.Symbol.Type.ToDisplayString()}`", declarator.Symbol.Name);
            }
        }

        private void HandleSimpleAssignment(ISimpleAssignmentOperation assign)
        {
            Walk(assign.Value, skipMovedRoot: true);

            var target = UnwrapConversion(assign.Target);
            var loc = assign.Syntax.GetLocation();

            if (target is IDiscardOperation)
            {
                DiscardValue(assign.Value, loc);
                return;
            }

            ISymbol? sym = target switch
            {
                ILocalReferenceOperation lr => lr.Local,
                IParameterReferenceOperation pr => pr.Parameter,
                _ => null
            };

            if (sym is not null)
            {
                var symType = GetSymbolType(sym);
                if (IsToken(symType))
                {
                    AssignToLinearTarget(sym, assign.Value, loc);
                }
                else if (HoldsTokenIndirectly(symType))
                {
                    Escape(assign.Value, loc, $"was stored in a variable of type `{symType.ToDisplayString()}`", sym.Name);
                }

                return;
            }

            // A field, property, array element, ... : the analyzer cannot follow a token stored there.
            if (VerifiableSymbolHelpers.ContainsMustConsumeElement(UnwrapConversion(assign.Value).Type, out _))
            {
                Escape(assign.Value, loc, "was stored in a field, property or array element");
            }
        }

        /// <summary><c>_ = value</c>: throwing a token away without saying why.</summary>
        private void DiscardValue(IOperation value, Location loc)
        {
            var rhs = UnwrapConversion(value);
            if (consumedExpressions.Contains(rhs) ||
                !VerifiableSymbolHelpers.ContainsMustConsumeElement(rhs.Type, out var linearType))
            {
                return;
            }

            if (TryResolve(rhs, out var r))
            {
                foreach (var movedSym in r.Symbols)
                {
                    if (!state.TryGetValue(movedSym, out var curSt))
                    {
                        ReportDroppedDiscard(rhs);
                        continue;
                    }

                    if (LinearTransfer.DiscardEvent(curSt.Kernel) == TransferEvent.DiscardedLive)
                    {
                        var originLoc = movedSym.Locations.FirstOrDefault();
                        Report(
                            loc,
                            originLoc,
                            originLoc is { IsInSource: true } ? "This obligation was created or received here:" : null,
                            "UNCONSUMED OBLIGATION",
                            movedSym.Name,
                            linearType!.Name,
                            "was explicitly discarded with `_` without being consumed",
                            $"Pass `{movedSym.Name}` to a `[ConsumesObligation]` method, or document intentional cancellation with `Obligation.Discard({movedSym.Name}, \"reason\")`.");
                    }

                    state[movedSym] = new TokenState(LinearTransfer.Discard(curSt.Kernel), true);
                }
            }
            else
            {
                Report(
                    loc,
                    null,
                    null,
                    "DROPPED OBLIGATION",
                    "_",
                    linearType!.Name,
                    "was explicitly discarded with `_` without being consumed",
                    "Store the obligation in a named variable and pass it to a `[ConsumesObligation]` method, or call `Obligation.Discard(..., \"reason\")`.");
            }
        }

        private void HandleDeconstructionAssignment(IDeconstructionAssignmentOperation deconstruct)
        {
            Walk(deconstruct.Value, skipMovedRoot: true);

            var pairs = new List<(IOperation Target, IOperation? Source)>();
            CollectPairs(deconstruct.Target, UnwrapConversion(deconstruct.Value), pairs);

            // Every value on the right is read before any variable on the left is written, so a swap
            // `(a, b) = (b, a)` first moves both tokens out and then gives both variables a token back.
            foreach (var (target, source) in pairs)
            {
                if (source is not null && IsToken(source.Type) && IsToken(target.Type))
                {
                    Move(source, source.Syntax.GetLocation(), ConsumeContext.Assignment);
                }
            }

            Snapshot();

            foreach (var (target, source) in pairs)
            {
                if (!IsToken(target.Type))
                {
                    continue;
                }

                var unwrappedTarget = UnwrapConversion(target);
                switch (unwrappedTarget)
                {
                    case ILocalReferenceOperation lr:
                        AssignFreshLinearTarget(lr.Local, unwrappedTarget.Syntax.GetLocation());
                        break;
                    case IParameterReferenceOperation pr:
                        AssignFreshLinearTarget(pr.Parameter, unwrappedTarget.Syntax.GetLocation());
                        break;
                    case IDiscardOperation:
                        if (source is null)
                        {
                            ReportDroppedDiscard(target);
                        }
                        else if (!TryResolve(source, out _))
                        {
                            // A fresh token that nothing receives. (A token moved out of a variable is already consumed.)
                            ReportDroppedDiscard(target);
                        }
                        break;
                    default:
                        Report(
                            unwrappedTarget.Syntax.GetLocation(),
                            null,
                            null,
                            "UNTRACKED OBLIGATION",
                            "<temporary>",
                            target.Type?.Name ?? "token",
                            "was stored in a field, property or array element, where the analyzer cannot follow it",
                            UntrackedFixHint);
                        break;
                }
            }
        }

        private void ReportDroppedDiscard(IOperation target) =>
            Report(
                target.Syntax.GetLocation(),
                null,
                null,
                "DROPPED OBLIGATION",
                "_",
                target.Type?.Name ?? "token",
                "was explicitly discarded with `_` without being consumed",
                "Store the obligation in a named variable and pass it to a `[ConsumesObligation]` method, or call `Obligation.Discard(..., \"reason\")`.");

        /// <summary>
        /// Pairs each variable on the left of a deconstruction with the value it receives, looking through nested
        /// tuples. A value that is not a tuple literal (a call, say) gives no pairing: its elements are fresh tokens.
        /// </summary>
        private static void CollectPairs(IOperation target, IOperation? source, List<(IOperation Target, IOperation? Source)> pairs)
        {
            target = UnwrapConversion(target);
            if (target is IDeclarationExpressionOperation decl)
            {
                target = UnwrapConversion(decl.Expression);
            }

            if (target is ITupleOperation tupleTarget)
            {
                var sourceTuple = source is not null ? UnwrapConversion(source) as ITupleOperation : null;
                var matches = sourceTuple is not null && sourceTuple.Elements.Length == tupleTarget.Elements.Length;
                for (var i = 0; i < tupleTarget.Elements.Length; i++)
                {
                    CollectPairs(tupleTarget.Elements[i], matches ? sourceTuple!.Elements[i] : null, pairs);
                }

                return;
            }

            pairs.Add((target, source));
        }

        private void AssignToLinearTarget(ISymbol targetSym, IOperation rhs, Location assignLoc)
        {
            var unwrappedRhs = UnwrapConversion(rhs);
            if (TryResolve(unwrappedRhs, out var r))
            {
                if (r.Exact && SymbolEqualityComparer.Default.Equals(r.Symbols[0], targetSym))
                {
                    return;
                }

                Move(unwrappedRhs, rhs.Syntax.GetLocation(), ConsumeContext.Assignment);
            }
            else
            {
                consumedExpressions.Add(unwrappedRhs);
            }

            // The right-hand side has been evaluated (and any sink it called owns its token), but nothing is
            // assigned yet: this is the state a throwing right-hand side leaves behind.
            Snapshot();

            AssignFreshLinearTarget(targetSym, assignLoc);
        }

        private void AssignFreshLinearTarget(ISymbol targetSym, Location assignLoc)
        {
            var hadState = state.TryGetValue(targetSym, out var existing);
            if (hadState && LinearTransfer.OverwriteEvent(existing.Kernel) == TransferEvent.LeakOnOverwrite)
            {
                var prevLoc = targetSym.Locations.FirstOrDefault();
                Report(
                    assignLoc,
                    prevLoc,
                    prevLoc is { IsInSource: true } ? "The previous obligation was bound here and never consumed:" : null,
                    "OBLIGATION OVERWRITTEN",
                    targetSym.Name,
                    GetTypeName(targetSym),
                    "was overwritten before its previous obligation was consumed",
                    $"Consume or discard the previous `{targetSym.Name}` before reassigning it.");
            }

            // A variable with no state yet simply starts holding a live token. A new token has not been consumed.
            state[targetSym] = new TokenState(hadState ? LinearTransfer.Overwrite(existing.Kernel) : ObligationState.Live, false);
        }

        // -------------------------------------------------------------------------------------------
        // Moves and consumption
        // -------------------------------------------------------------------------------------------

        /// <summary>The token variables an expression stands for, if it is a read of one (possibly through a flow capture).</summary>
        private bool TryResolve(IOperation op, out TokenRef resolved)
        {
            op = UnwrapConversion(op);
            switch (op)
            {
                case ILocalReferenceOperation lr:
                    resolved = new TokenRef(new ISymbol[] { lr.Local }, true);
                    return true;
                case IParameterReferenceOperation pr:
                    resolved = new TokenRef(new ISymbol[] { pr.Parameter }, true);
                    return true;
                case IFlowCaptureReferenceOperation fcr when analysis.TryGetCapture(fcr.Id, out var alias) && alias.Symbols.Count > 0:
                    resolved = new TokenRef(alias.Symbols, alias.Symbols.Count == 1 && !alias.HasOther);
                    return true;
                default:
                    resolved = default;
                    return false;
            }
        }

        /// <summary>
        /// Moves a token out of an expression: a token variable is consumed, a fresh token (a call result, say)
        /// is accounted for.
        /// </summary>
        private void Move(IOperation source, Location site, ConsumeContext context, bool reportUninitialized = false)
        {
            var unwrapped = UnwrapConversion(source);
            if (TryResolve(unwrapped, out var r))
            {
                foreach (var sym in r.Symbols)
                {
                    if (IsToken(GetSymbolTypeOrNull(sym)))
                    {
                        ConsumeSymbol(sym, weak: !r.Exact, site, context, reportUninitialized);
                    }
                }
            }
            else
            {
                consumedExpressions.Add(unwrapped);
            }
        }

        /// <summary>
        /// Consumes a token variable. A weak consume (the expression is one of several variables) leaves it
        /// maybe-live: it may or may not have been the one consumed.
        /// </summary>
        private void ConsumeSymbol(ISymbol sym, bool weak, Location site, ConsumeContext context, bool reportUninitialized)
        {
            if (!state.TryGetValue(sym, out var cur))
            {
                if (reportUninitialized)
                {
                    Report(
                        site,
                        null,
                        null,
                        "UNCONSUMED OBLIGATION",
                        sym.Name,
                        GetTypeName(sym),
                        "was consumed before being initialized",
                        $"Initialize `{sym.Name}` from a producer before passing it to a `[ConsumesObligation]` method.");
                }

                return;
            }

            var definite = LinearTransfer.ConsumeEvent(cur.Kernel) == TransferEvent.DoubleConsume;
            var possible = !definite && cur.MaybeConsumed;

            var consumed = LinearTransfer.Consume(cur.Kernel);
            if (!analysis.ConsumedSites.ContainsKey(sym))
            {
                analysis.ConsumedSites[sym] = site;
            }

            state[sym] = new TokenState(weak ? ObligationLattice.Join(cur.Kernel, consumed) : consumed, true);

            if (!definite && !possible)
            {
                return;
            }

            analysis.ConsumedSites.TryGetValue(sym, out var priorLoc);
            var detail = context switch
            {
                ConsumeContext.Assignment => definite
                    ? "was already consumed before assignment"
                    : "may already have been consumed on some path before assignment",
                ConsumeContext.Return => definite
                    ? "was already consumed before being returned"
                    : "may already have been consumed on some path before being returned",
                _ => definite
                    ? "was consumed more than once"
                    : "may be consumed more than once: it was already consumed on some path that reaches here"
            };
            var fix = context switch
            {
                ConsumeContext.Assignment => $"Remove the duplicate use of `{sym.Name}`, or create a fresh `{GetTypeName(sym)}` token.",
                ConsumeContext.Return => $"Either return `{sym.Name}` to transfer ownership to the caller, or consume it locally—not both.",
                _ => $"Remove this duplicate consumption of `{sym.Name}` so the obligation is discharged exactly once."
            };
            var heading = context switch
            {
                ConsumeContext.Assignment => "This obligation was already consumed or moved here:",
                _ => "It was already consumed here:"
            };

            Report(
                site,
                priorLoc,
                priorLoc is { IsInSource: true } ? heading : null,
                "OBLIGATION ALREADY CONSUMED",
                sym.Name,
                GetTypeName(sym),
                detail,
                fix);
        }

        private static readonly string UntrackedFixHint =
            "Keep the token in a local variable or parameter and pass it to a `[ConsumesObligation]` method, return it, or call `Obligation.Discard(...)`. Do not store tokens in arrays, tuples, `Nullable<T>`, fields or collections.";

        /// <summary>
        /// A token put where the analyzer cannot follow it (an array, a tuple, a <c>Nullable&lt;T&gt;</c>, a field).
        /// The token is treated as moved, so only this one problem is reported.
        /// </summary>
        private void Escape(IOperation value, Location loc, string what, string? holder = null)
        {
            var leaves = new List<IOperation>();
            CollectTokenLeaves(value, leaves);

            foreach (var leaf in leaves)
            {
                var name = TryResolve(leaf, out var r) ? r.Symbols[0].Name : holder ?? "<temporary>";
                var typeName = leaf.Type?.Name ?? "token";
                Move(leaf, loc, ConsumeContext.Other);
                Report(
                    loc,
                    null,
                    null,
                    "UNTRACKED OBLIGATION",
                    name,
                    typeName,
                    $"{what}, where the analyzer cannot follow it",
                    UntrackedFixHint);
            }

            if (leaves.Count == 0 &&
                VerifiableSymbolHelpers.ContainsMustConsumeElement(UnwrapConversion(value).Type, out var found))
            {
                Report(
                    loc,
                    null,
                    null,
                    "UNTRACKED OBLIGATION",
                    "<temporary>",
                    found!.Name,
                    $"{what}, where the analyzer cannot follow it",
                    UntrackedFixHint);
            }
        }

        private static void CollectTokenLeaves(IOperation value, List<IOperation> leaves)
        {
            var unwrapped = UnwrapConversion(value);
            if (unwrapped is ITupleOperation tuple)
            {
                foreach (var element in tuple.Elements)
                {
                    CollectTokenLeaves(element, leaves);
                }
            }
            else if (IsToken(unwrapped.Type))
            {
                leaves.Add(unwrapped);
            }
        }

        // -------------------------------------------------------------------------------------------
        // Returns
        // -------------------------------------------------------------------------------------------

        private void MarkReturnedOwnership(IOperation retVal)
        {
            var unwrapped = UnwrapConversion(retVal);
            if (TryResolve(unwrapped, out var r) && IsToken(GetSymbolTypeOrNull(r.Symbols[0])))
            {
                foreach (var sym in r.Symbols)
                {
                    ConsumeSymbol(sym, weak: !r.Exact, unwrapped.Syntax.GetLocation(), ConsumeContext.Return, reportUninitialized: false);
                }

                returnOwned.Add(unwrapped);
                return;
            }

            // Support returning wrapped obligations inside tuples, Result<T, E>.Ok(token), or immutable record constructors
            returnOwned.Add(unwrapped);
            if (unwrapped is ITupleOperation tuple)
            {
                foreach (var elem in tuple.Elements)
                {
                    MarkReturnedOwnership(elem);
                }
            }
            else if (unwrapped is IInvocationOperation inv &&
                     VerifiableSymbolHelpers.HasAttribute(inv.TargetMethod, VerifiableSymbolHelpers.PureAxiomAttr))
            {
                foreach (var arg in inv.Arguments)
                {
                    MarkReturnedOwnership(arg.Value);
                }
            }
            else if (unwrapped is IObjectCreationOperation obj)
            {
                foreach (var arg in obj.Arguments)
                {
                    MarkReturnedOwnership(arg.Value);
                }
            }
        }

        // -------------------------------------------------------------------------------------------
        // Reads: the general walk
        // -------------------------------------------------------------------------------------------

        private void Walk(IOperation op, bool skipMovedRoot = false)
        {
            // Note: handle assignments/declarators embedded inside other expressions (e.g., nested assignment)
            switch (op)
            {
                case IVariableDeclaratorOperation v:
                    HandleVariableDeclarator(v);
                    return;
                case ISimpleAssignmentOperation sa:
                    HandleSimpleAssignment(sa);
                    return;
                case IDeconstructionAssignmentOperation da:
                    HandleDeconstructionAssignment(da);
                    return;
                case IBlockOperation:
                    return; // nested blocks/lambdas have their own CFG
                case IInvocationOperation inv:
                    WalkInvocation(inv);
                    return;
                case IObjectCreationOperation creation:
                    WalkObjectCreation(creation);
                    return;
                case IArrayInitializerOperation init:
                    WalkArrayInitializer(init);
                    return;
                case ICollectionExpressionOperation collection:
                    foreach (var element in collection.Elements)
                    {
                        if (CollectTokenLeaf(element) is { } leaf)
                        {
                            Escape(leaf, element.Syntax.GetLocation(), "was put in a collection");
                        }
                        else
                        {
                            Walk(element);
                        }
                    }
                    return;
                case ITupleOperation tuple when !returnOwned.Contains(tuple) && IsTupleHoldingTokens(tuple) && !TupleIsAccountedFor(tuple):
                    Escape(tuple, tuple.Syntax.GetLocation(), "was put in a tuple");
                    foreach (var element in tuple.Elements)
                    {
                        if (!IsToken(UnwrapConversion(element).Type))
                        {
                            Walk(element);
                        }
                    }
                    return;
                case IConversionOperation conv when IsConversionToNullableToken(conv) && !IsAssignedToVariable(conv):
                    Escape(conv, conv.Syntax.GetLocation(), "was stored in a `Nullable<T>`");
                    return;
                case IFieldReferenceOperation or IPropertyReferenceOperation when IsUntrackableTokenRead(op):
                    Report(
                        op.Syntax.GetLocation(),
                        null,
                        null,
                        "UNTRACKED OBLIGATION",
                        MemberName(op),
                        op.Type!.Name,
                        $"was read from the member `{MemberName(op)}`, where the analyzer cannot tell whether the obligation was already consumed",
                        $"Token read from member `{MemberName(op)}` cannot be tracked; pass the token itself. Keep tokens in local variables or parameters, not in fields or properties.");
                    return;
            }

            if (!skipMovedRoot && TryResolve(op, out var resolved) && IsToken(GetSymbolTypeOrNull(resolved.Symbols[0])))
            {
                var unwrappedOp = UnwrapConversion(op);
                if (!returnOwned.Contains(unwrappedOp) && !consumedExpressions.Contains(unwrappedOp))
                {
                    foreach (var sym in resolved.Symbols)
                    {
                        if (state.TryGetValue(sym, out var cur) &&
                            LinearTransfer.ReadEvent(cur.Kernel) == TransferEvent.UseAfterConsume)
                        {
                            analysis.ConsumedSites.TryGetValue(sym, out var priorLoc);
                            Report(
                                op.Syntax.GetLocation(),
                                priorLoc,
                                priorLoc is { IsInSource: true } ? "It was already consumed or moved here:" : null,
                                "OBLIGATION ALREADY CONSUMED",
                                sym.Name,
                                GetTypeName(sym),
                                "was used after being consumed or moved",
                                $"Read any fields you need from `{sym.Name}` before passing it to a `[ConsumesObligation]` method or moving it.");
                        }
                    }
                }

                return;
            }

            // If a nested sub-expression produces a [MustConsume] value inside a non-returning, non-assigning, non-consuming expression
            foreach (var child in op.ChildOperations)
            {
                var childSkip = skipMovedRoot && UnwrapConversion(op) == UnwrapConversion(child);
                Walk(child, childSkip);
            }
        }

        private static string MemberName(IOperation op) => op switch
        {
            IFieldReferenceOperation f => f.Field.Name,
            IPropertyReferenceOperation pr => pr.Property.Name,
            _ => "<member>"
        };

        /// <summary>
        /// A token read out of a field or property of an object that is not itself a token. Every read yields the
        /// same obligation again, which the analyzer cannot follow. Reading a field of a token (<c>p.Id</c>) is
        /// not a token read, and neither is reading a member of a token that is held in such a member.
        /// </summary>
        private static bool IsUntrackableTokenRead(IOperation op)
        {
            if (!IsToken(op.Type))
            {
                return false;
            }

            var member = op switch
            {
                IFieldReferenceOperation f => (ISymbol)f.Field,
                IPropertyReferenceOperation pr => pr.Property,
                _ => null
            };
            if (member is null || IsToken(member.ContainingType))
            {
                return false;
            }

            // `holder.Tok.Id`: only a field of the token is read, the obligation is untouched.
            var parent = op.Parent;
            while (parent is IConversionOperation or IParenthesizedOperation)
            {
                parent = parent.Parent;
            }

            var receiver = parent switch
            {
                IFieldReferenceOperation f => f.Instance,
                IPropertyReferenceOperation pr => pr.Instance,
                _ => null
            };
            return receiver is null || UnwrapConversion(receiver) != op;
        }

        private static IOperation? CollectTokenLeaf(IOperation element) =>
            IsToken(UnwrapConversion(element).Type) ? element : null;

        private void WalkArrayInitializer(IArrayInitializerOperation init)
        {
            foreach (var element in init.ElementValues)
            {
                if (element is IArrayInitializerOperation nested)
                {
                    WalkArrayInitializer(nested);
                }
                else if (CollectTokenLeaf(element) is { } leaf)
                {
                    Escape(leaf, element.Syntax.GetLocation(), "was put in an array");
                }
                else
                {
                    Walk(element);
                }
            }
        }

        private static bool IsTupleHoldingTokens(ITupleOperation tuple) =>
            tuple.Elements.Any(e => IsToken(UnwrapConversion(e).Type) || UnwrapConversion(e) is ITupleOperation inner && IsTupleHoldingTokens(inner));

        /// <summary>A tuple literal that the assignment, deconstruction or return around it accounts for.</summary>
        private bool TupleIsAccountedFor(ITupleOperation tuple)
        {
            IOperation current = tuple;
            var parent = current.Parent;
            while (parent is IConversionOperation or IParenthesizedOperation)
            {
                current = parent;
                parent = parent.Parent;
            }

            return parent switch
            {
                IDeconstructionAssignmentOperation => true,
                ISimpleAssignmentOperation => true,
                IVariableInitializerOperation => true,
                IVariableDeclaratorOperation => true,
                IReturnOperation => true,
                ITupleOperation => true,
                _ => false
            };
        }

        private static bool IsConversionToNullableToken(IConversionOperation conv) =>
            conv.Type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable &&
            nullable.TypeArguments.Length == 1 &&
            IsToken(nullable.TypeArguments[0]) &&
            IsToken(conv.Operand.Type);

        /// <summary>A conversion that feeds an assignment or declaration, which reports the nullable itself.</summary>
        private static bool IsAssignedToVariable(IConversionOperation conv) =>
            conv.Parent is ISimpleAssignmentOperation or IVariableInitializerOperation;

        // -------------------------------------------------------------------------------------------
        // Calls
        // -------------------------------------------------------------------------------------------

        private void WalkInvocation(IInvocationOperation inv)
        {
            CheckTemporary(inv);

            var target = inv.TargetMethod;
            var consumesArgs = VerifiableSymbolHelpers.HasAttribute(target, VerifiableSymbolHelpers.ConsumesObligationAttr);
            var instanceConsumed = false;
            if (inv.Instance is not null)
            {
                var instance = UnwrapConversion(inv.Instance);
                var instanceSinks = consumesArgs;
                if (!instanceSinks && IsToken(instance.Type) && target.ContainingType.TypeKind == TypeKind.Interface)
                {
                    // `using` calls `IDisposable.Dispose`; what counts is the token type's own implementation.
                    instanceSinks = instance.Type!.FindImplementationForInterfaceMember(target) is IMethodSymbol impl &&
                        VerifiableSymbolHelpers.HasAttribute(impl, VerifiableSymbolHelpers.ConsumesObligationAttr);
                }

                if (instanceSinks && IsToken(instance.Type) && TryResolve(instance, out var self))
                {
                    // A `[ConsumesObligation]` method on the token itself (such as `Dispose`) takes the token over.
                    instanceConsumed = true;
                    foreach (var sym in self.Symbols)
                    {
                        ConsumeSymbol(sym, weak: !self.Exact, inv.Syntax.GetLocation(), ConsumeContext.Argument, reportUninitialized: self.Exact);
                    }
                }
                else
                {
                    Walk(inv.Instance);
                }
            }

            var consumedAny = instanceConsumed;
            consumedAny |= WalkArguments(inv.Arguments, target, consumesArgs, inv, out var firstConsumedName);

            if (target.MethodKind == MethodKind.LocalFunction)
            {
                ApplyLocalFunctionSummary(inv);
            }

            Snapshot();
            AssignOutArguments(inv.Arguments);

            if (consumesArgs && consumedAny && IsAwaitable(inv.Type) && !IsAwaitedOrReturned(inv))
            {
                Report(
                    inv.Syntax.GetLocation(),
                    null,
                    null,
                    "UNCONSUMED OBLIGATION",
                    firstConsumedName ?? "<temporary>",
                    inv.Arguments.FirstOrDefault(a => IsToken(UnwrapConversion(a.Value).Type))?.Value.Type?.Name ?? "token",
                    $"was passed to the async method `{target.Name}`, but the returned task is not awaited or returned, so nothing shows the obligation was discharged",
                    $"`await {target.Name}(...)` the call, or return its task to the caller.");
            }
        }

        private void WalkObjectCreation(IObjectCreationOperation creation)
        {
            CheckTemporary(creation);

            var constructor = creation.Constructor;
            var consumesArgs = constructor is not null &&
                VerifiableSymbolHelpers.HasAttribute(constructor, VerifiableSymbolHelpers.ConsumesObligationAttr);

            WalkArguments(creation.Arguments, constructor, consumesArgs, creation, out _);
            AssignOutArguments(creation.Arguments);

            if (creation.Initializer is { } initializer)
            {
                Walk(initializer);
            }
        }

        /// <summary>
        /// Handles the arguments of a call: a token passed to a sink is consumed. A token passed to anything else
        /// is moved into a method that is not marked as taking it over, which is reported.
        /// </summary>
        private bool WalkArguments(
            ImmutableArray<IArgumentOperation> arguments,
            IMethodSymbol? target,
            bool consumesArgs,
            IOperation owner,
            out string? firstConsumedName)
        {
            firstConsumedName = null;
            var consumedAny = false;

            foreach (var arg in arguments)
            {
                var argVal = UnwrapConversion(arg.Value);
                var refKind = arg.Parameter?.RefKind ?? RefKind.None;

                if (refKind == RefKind.Out)
                {
                    // Assigned when the call returns (see AssignOutArguments).
                    continue;
                }

                if (!IsToken(argVal.Type))
                {
                    Walk(arg.Value);
                    continue;
                }

                var argName = TryResolve(argVal, out var named) ? named.Symbols[0].Name : "<temporary>";

                if (consumesArgs)
                {
                    consumedAny = true;
                    firstConsumedName ??= argName;
                    if (TryResolve(argVal, out var r))
                    {
                        foreach (var sym in r.Symbols)
                        {
                            ConsumeSymbol(sym, weak: !r.Exact, arg.Syntax.GetLocation(), ConsumeContext.Argument, reportUninitialized: r.Exact);
                        }
                    }
                    else
                    {
                        Walk(argVal, skipMovedRoot: true);
                        consumedExpressions.Add(argVal);
                    }
                }
                else if (refKind == RefKind.In || returnOwned.Contains(owner) || !IsToken(arg.Parameter?.Type))
                {
                    // Borrowed (`in`), handed on as part of a returned value, or copied into a parameter that is not a token.
                    Walk(arg.Value);
                }
                else
                {
                    // The callee takes the token by value but is not a sink: nothing tells the analyzer whether
                    // it consumes the token, so the call is rejected, and the token counts as moved.
                    var targetName = target?.Name ?? "the method";
                    Walk(argVal, skipMovedRoot: true);
                    Move(argVal, arg.Syntax.GetLocation(), ConsumeContext.Argument);
                    Report(
                        arg.Syntax.GetLocation(),
                        null,
                        null,
                        "UNCONSUMED OBLIGATION",
                        argName,
                        argVal.Type?.Name ?? "token",
                        $"was passed to `{targetName}`, which is not a `[ConsumesObligation]` method, so nothing discharges the obligation",
                        $"Mark `{targetName}` with `[ConsumesObligation]` so it takes over the token, or pass `{argName}` only to `[ConsumesObligation]` methods.");
                }
            }

            return consumedAny;
        }

        /// <summary>The tokens that come back through <c>out</c> arguments: each one is a fresh live token.</summary>
        private void AssignOutArguments(ImmutableArray<IArgumentOperation> arguments)
        {
            foreach (var arg in arguments)
            {
                if (arg.Parameter?.RefKind != RefKind.Out || !IsToken(arg.Parameter.Type))
                {
                    continue;
                }

                var value = UnwrapConversion(arg.Value);
                if (value is IDeclarationExpressionOperation decl)
                {
                    value = UnwrapConversion(decl.Expression);
                }

                switch (value)
                {
                    case ILocalReferenceOperation lr:
                        AssignFreshLinearTarget(lr.Local, arg.Syntax.GetLocation());
                        break;
                    case IParameterReferenceOperation pr:
                        AssignFreshLinearTarget(pr.Parameter, arg.Syntax.GetLocation());
                        break;
                    case IDiscardOperation:
                        ReportDroppedDiscard(value);
                        break;
                    default:
                        Report(
                            arg.Syntax.GetLocation(),
                            null,
                            null,
                            "UNTRACKED OBLIGATION",
                            "<temporary>",
                            arg.Parameter.Type.Name,
                            "was stored in a field, property or array element through an `out` argument, where the analyzer cannot follow it",
                            UntrackedFixHint);
                        break;
                }
            }
        }

        private void ApplyLocalFunctionSummary(IInvocationOperation inv)
        {
            var summary = analysis.Session.GetSummary(inv.TargetMethod);
            foreach (var kv in summary.Exit)
            {
                var st = kv.Value;
                if (st.Kernel == ObligationState.Live && !st.MaybeConsumed)
                {
                    continue; // the local function leaves this token alone
                }

                ConsumeSymbol(
                    kv.Key,
                    weak: st.Kernel != ObligationState.Consumed,
                    inv.Syntax.GetLocation(),
                    ConsumeContext.Argument,
                    reportUninitialized: false);
            }
        }

        // -------------------------------------------------------------------------------------------
        // Temporaries
        // -------------------------------------------------------------------------------------------

        /// <summary>
        /// A call that makes a token must hand it to something that takes it over. Used as the receiver of a member
        /// read (<c>Create(1).Id</c>), an operand or the like, the token is dropped on the spot.
        /// </summary>
        private void CheckTemporary(IOperation producer)
        {
            if (!IsToken(producer.Type) ||
                returnOwned.Contains(producer) ||
                consumedExpressions.Contains(producer) ||
                IsAbsorbedByParent(producer))
            {
                return;
            }

            Report(
                producer.Syntax.GetLocation(),
                null,
                null,
                "DROPPED OBLIGATION",
                "<temporary>",
                producer.Type!.Name,
                "was produced as a temporary and dropped without being stored or consumed",
                "Bind the returned obligation to a local variable (`var token = ...;`) and pass it to a `[ConsumesObligation]` method, return it, or call `Obligation.Discard(...)`.");
        }

        /// <summary>Whether the construct around a freshly made token takes responsibility for it (or reports it itself).</summary>
        private static bool IsAbsorbedByParent(IOperation producer)
        {
            var parent = producer.Parent;
            while (parent is IConversionOperation or IParenthesizedOperation)
            {
                parent = parent.Parent;
            }

            return parent switch
            {
                null => true,
                IExpressionStatementOperation => true, // reported as a dropped statement value
                IArgumentOperation => true,            // a sink consumes it; anything else is reported at the call
                ISimpleAssignmentOperation => true,
                IVariableInitializerOperation => true,
                IVariableDeclaratorOperation => true,
                IReturnOperation => true,
                IFlowCaptureOperation => true,
                IDeconstructionAssignmentOperation => true,
                ITupleOperation => true,
                IArrayInitializerOperation => true,
                IAwaitOperation => true,
                _ => false
            };
        }

        private bool IsAwaitedOrReturned(IInvocationOperation inv)
        {
            var parent = inv.Parent;
            while (parent is IConversionOperation or IParenthesizedOperation)
            {
                parent = parent.Parent;
            }

            // CommitAsync(t).ConfigureAwait(false) is still the same task.
            while (parent is IInvocationOperation { TargetMethod.Name: "ConfigureAwait" or "AsTask" or "WaitAsync" } wrapper &&
                   wrapper.Instance is not null &&
                   UnwrapConversion(wrapper.Instance) == UnwrapConversion(inv))
            {
                inv = wrapper;
                parent = wrapper.Parent;
                while (parent is IConversionOperation or IParenthesizedOperation)
                {
                    parent = parent.Parent;
                }
            }

            if (parent is IAwaitOperation or IReturnOperation || returnOwned.Contains(UnwrapConversion(inv)))
            {
                return true;
            }

            // Task.WhenAll(CommitAsync(a), CommitAsync(b)) waits for all of them.
            while (parent is IArgumentOperation or IArrayInitializerOperation or IArrayCreationOperation or ICollectionExpressionOperation or IConversionOperation)
            {
                parent = parent.Parent;
            }

            return parent is IInvocationOperation
            {
                TargetMethod:
                {
                    ContainingType.Name: "Task",
                    Name: "WhenAll" or "WhenAny" or "WaitAll" or "WaitAny"
                }
            };
        }
    }
}
