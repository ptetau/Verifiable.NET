using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;
using Verifiable.Shared;

namespace Verifiable.Analyzers.Smt;

/// <summary>
/// Loops and recursion: what a loop's contracts must look like, why every loop and every recursive call needs a
/// termination argument, how <c>foreach</c> over a sequence is modelled, and the proof obligation of a
/// non-exhaustive switch expression.
/// </summary>
/// <remarks>
/// A proof of a postcondition only means something on the paths that return. A loop that may never end, or a call
/// that may never return, makes everything after it unreachable, so a callee's <c>Spec.Ensures</c> would be assumed
/// for a call that never gives a result. That is why <c>while</c>, <c>do</c> and <c>for</c> loops carry a
/// <c>Spec.Decreases</c>, and why a recursive call needs a measure that goes down.
/// </remarks>
internal sealed partial class CfgSsaLowerer
{
    /// <summary>Takes an edge that leaves the loop being lowered: to the code after it, or into an enclosing loop.</summary>
    private delegate void EdgeSink(BasicBlock src, BasicBlock dst, string gamma, BlockEnv env);

    /// <summary>The natural loop of each header, for the method being lowered.</summary>
    private Dictionary<BasicBlock, HashSet<BasicBlock>> _loopBlocksByHeader = new();

    /// <summary>
    /// Marks a <see cref="_errors"/> entry as a rule about loops, recursion or switch expressions rather than code that
    /// could not be translated. The text after it is <c>headline</c>, a newline, <c>Why: ...</c>, a newline and <c>Fix: ...</c>.
    /// </summary>
    internal const string RuleMarker = "[rule] ";

    private static string RuleText(string headline, string why, string fix) =>
        $"{RuleMarker}{headline}\nWhy: {why}\nFix: {fix}";

    /// <summary>The statements of a block, and the value that decides where it goes next.</summary>
    private static IEnumerable<IOperation> LoopBlockOperations(BasicBlock block)
    {
        foreach (var op in block.Operations)
        {
            yield return op;
        }

        if (block.BranchValue is { } branchValue)
        {
            yield return branchValue;
        }
    }

    /// <summary>The span from the first to the last operation in the loop's blocks, or null if they hold none.</summary>
    private static TextSpan? CommonSpanOfLoopOperations(HashSet<BasicBlock> loopBody)
    {
        var start = int.MaxValue;
        var end = -1;
        foreach (var block in loopBody)
        {
            foreach (var op in LoopBlockOperations(block))
            {
                var span = op.Syntax.Span;
                start = Math.Min(start, span.Start);
                end = Math.Max(end, span.End);
            }
        }

        return end < 0 ? null : TextSpan.FromBounds(start, end);
    }

    /// <summary>
    /// The <c>Spec.*</c> calls at the top of the loop body: its leading statements as long as each is a call of
    /// <c>Spec</c>. Loop contracts count only there, like <c>Spec.Requires</c> at the top of a method.
    /// </summary>
    private static HashSet<SyntaxNode> LeadingLoopContractCalls(ILoopOperation loop)
    {
        var result = new HashSet<SyntaxNode>();
        IEnumerable<IOperation> statements = loop.Body is IBlockOperation block
            ? block.Operations
            : new[] { loop.Body };
        foreach (var statement in statements)
        {
            if (statement is IExpressionStatementOperation { Operation: IInvocationOperation inv } &&
                inv.TargetMethod.ContainingType?.ToDisplayString() == VerifiableSymbolHelpers.SpecTypeName)
            {
                result.Add(inv.Syntax);
                continue;
            }

            break;
        }

        return result;
    }

    /// <summary>Reports a loop whose contracts are misplaced, and a <c>while</c>, <c>do</c> or <c>for</c> loop with no measure.</summary>
    private void CheckLoopRules(LoopSpecGroup? group, Location headerLoc)
    {
        var display = $"{_method.ContainingType?.Name}.{_method.Name}";

        if (group is not null)
        {
            foreach (var (loc, text) in group.Misplaced)
            {
                var name = text.Contains("Decreases") ? "Spec.Decreases" : "Spec.LoopInvariant";
                _errors.Add((text, loc, RuleText(
                    $"`{name}` is not at the top of its loop body in `{display}`.",
                    "A loop contract is checked where the loop is entered and at the top of every pass. Written after another statement, it would still be checked at the top, not where it stands, so it would not mean what it says.",
                    $"Move it to the top of the loop body, next to the other loop contracts. A fact that holds only after some statement is not a loop invariant; state it with `if` or fold it into the invariant at the top.")));
            }
        }

        var needsMeasure = group is null || group.Loop is IWhileLoopOperation or IForLoopOperation;
        if (needsMeasure && (group is null || group.Decreases.Count == 0))
        {
            var kind = group?.Loop switch
            {
                IForLoopOperation => "`for`",
                IWhileLoopOperation { ConditionIsTop: false } => "`do`",
                _ => "`while`",
            };
            var location = group?.Loop.Syntax.GetLocation() ?? headerLoc;
            _errors.Add((group?.Loop.Syntax.ToString().Split('\n')[0].Trim() ?? "loop", location, RuleText(
                $"This {kind} loop in `{display}` has no `Spec.Decreases(...)`, so nothing shows that it ends.",
                "A proof about what a method returns only means something if the method returns. A loop that may run forever makes everything after it unreachable, so those proofs would hold without ever being used. Only `foreach` over a finite collection ends on its own.",
                "Add `Spec.Decreases(measure, \"why\")` at the top of the loop body: a non-negative `int` or `long` expression that goes down on every pass, for example `Spec.Decreases(n - i, \"i moves toward n.\")`.")));
        }
    }

    // ---- implicit SwitchExpressionException ----

    /// <summary>
    /// A switch expression that does not cover every value throws <c>SwitchExpressionException</c> for the rest. The
    /// compiler adds a block that ends in that throw; reaching it is a failure of the method, so the block must be
    /// unreachable: a goal of <c>false</c> under the path condition that leads to it.
    /// </summary>
    private bool EmitImplicitSwitchThrowObligation(BasicBlock block, string gamma)
    {
        if (block.ConditionalSuccessor is not null ||
            block.FallThroughSuccessor is not { Semantics: ControlFlowBranchSemantics.Throw } ||
            block.BranchValue is not IObjectCreationOperation { IsImplicit: true } created ||
            created.Type?.ToDisplayString() != "System.Runtime.CompilerServices.SwitchExpressionException")
        {
            return false;
        }

        AddObligation(
            "SwitchExpressionException",
            "Switch expression covers every value that reaches it",
            created.Syntax.GetLocation(),
            gamma,
            "false",
            "A value that matches no arm makes the switch expression throw SwitchExpressionException.");
        return true;
    }

    // ---- recursion ----

    /// <summary>The call graph of source methods, shared by every lowerer of one compilation.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Compilation, System.Collections.Concurrent.ConcurrentDictionary<IMethodSymbol, List<IMethodSymbol>>> CallGraphs = new();

    private readonly Dictionary<IMethodSymbol, bool> _recursiveCalls = new(SymbolEqualityComparer.Default);
    private bool _reportedRecursionWithoutMeasure;

    /// <summary>
    /// A call that can lead back into the method being lowered (itself or through other <c>[Verifiable]</c> methods)
    /// must show that the call chain ends: the callee's measure is non-negative and below this method's measure on
    /// entry. Without it the callee's <c>Spec.Ensures</c> would be assumed for a call that may never return.
    /// </summary>
    private void CheckRecursiveCall(IMethodSymbol callee, SmtTypedTerm? receiver, IReadOnlyList<SmtTypedTerm> args, string gamma, Location loc)
    {
        if (!IsRecursiveCall(callee))
        {
            return;
        }

        var display = $"{_method.ContainingType?.Name}.{_method.Name}";
        var calleeDisplay = $"{callee.ContainingType?.Name}.{callee.Name}";
        var isSelf = SymbolEqualityComparer.Default.Equals(callee.OriginalDefinition, _method.OriginalDefinition);

        var why = "A call chain that comes back to the same method may never return, and then the callee's `Spec.Ensures` would be assumed for a result that never arrives. A measure that goes down on every recursive call and cannot go below zero shows that the chain ends.";
        const string fix = "Add `Spec.Decreases(measure, \"why\")` to the top of the method: a non-negative `int` or `long` expression over its parameters that is smaller at every recursive call, for example `Spec.Decreases(n, \"n goes down by one.\")`.";

        if (_currentSpecs.MethodDecreases.Count != 1)
        {
            if (!_reportedRecursionWithoutMeasure)
            {
                _reportedRecursionWithoutMeasure = true;
                var problem = _currentSpecs.MethodDecreases.Count == 0
                    ? $"`{display}` {(isSelf ? "calls itself" : $"calls `{calleeDisplay}`, which can call it back")} but has no `Spec.Decreases(...)`, so nothing shows that the recursion ends."
                    : $"`{display}` has more than one `Spec.Decreases(...)`; a recursive method takes exactly one measure.";
                _errors.Add((calleeDisplay, loc, RuleText(problem, why, fix)));
            }
            return;
        }

        var calleeSpecs = isSelf ? _currentSpecs : ExtractMethodSpecs(_compilation, callee, default);
        if (calleeSpecs.MethodDecreases.Count != 1)
        {
            _errors.Add((calleeDisplay, loc, RuleText(
                calleeSpecs.MethodDecreases.Count == 0
                    ? $"`{display}` calls `{calleeDisplay}`, which can call it back, but `{calleeDisplay}` has no `Spec.Decreases(...)`, so nothing shows that the recursion ends."
                    : $"`{calleeDisplay}` has more than one `Spec.Decreases(...)`; a recursive method takes exactly one measure.",
                why, fix)));
            return;
        }

        var callerMeasure = _currentSpecs.MethodDecreases[0];
        var calleeMeasure = calleeSpecs.MethodDecreases[0];

        try
        {
            var entryEnv = new BlockEnv { ThisOverride = _entryThis };
            foreach (var kv in _entryParamVars)
            {
                entryEnv.Vars[kv.Key] = kv.Value;
            }

            var calleeEnv = new BlockEnv { ThisOverride = receiver };
            for (var i = 0; i < callee.Parameters.Length && i < args.Count; i++)
            {
                calleeEnv.Vars[callee.Parameters[i]] = args[i];
            }

            // The caller's own measure is checked once, at entry; the callee's is evaluated at this call.
            var before = LowerExpression(callerMeasure.Measure, gamma, entryEnv, emitObligations: false);
            var after = LowerSpecGoal(calleeMeasure.Measure, gamma, calleeEnv);
            var (beforeTerm, afterTerm, unified) = _translator.UnifyNumericPair(before, after);
            var sort = _types.GetSort(unified);
            var isBv = ContractExpressionTranslator.IsBitVecSort(sort);
            var zero = _translator.FormatIntLiteral(0, sort == "(_ BitVec 64)" ? 64 : 32);
            AddObligation(
                "RecursionDecreases",
                $"{calleeMeasure.DisplayText} decreases at the call to {calleeDisplay}",
                loc,
                gamma,
                $"(and ({(isBv ? "bvsge" : ">=")} {afterTerm} {zero}) ({(isBv ? "bvslt" : "<")} {afterTerm} {beforeTerm}))",
                calleeMeasure.Because ?? callerMeasure.Because,
                calleeMeasure.Location);
        }
        catch (ContractLoweringException ex)
        {
            _errors.Add((calleeMeasure.DisplayText, calleeMeasure.Location, ex.Message));
        }
    }

    private bool IsRecursiveCall(IMethodSymbol callee)
    {
        if (!_recursiveCalls.TryGetValue(callee.OriginalDefinition, out var known))
        {
            known = FindsWayBack(callee);
            _recursiveCalls[callee.OriginalDefinition] = known;
        }

        return known;
    }

    private bool FindsWayBack(IMethodSymbol callee)
    {
        var target = _method.OriginalDefinition;
        var start = callee.OriginalDefinition;
        if (VerifiableSymbolHelpers.GetScope(start) != VerificationScope.Verifiable)
        {
            return false;
        }

        if (SymbolEqualityComparer.Default.Equals(start, target))
        {
            return true;
        }

        var visited = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        var stack = new Stack<IMethodSymbol>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!visited.Add(current))
            {
                continue;
            }

            foreach (var next in CalledSourceMethods(current))
            {
                if (SymbolEqualityComparer.Default.Equals(next, target))
                {
                    return true;
                }

                if (VerifiableSymbolHelpers.GetScope(next) == VerificationScope.Verifiable)
                {
                    stack.Push(next);
                }
            }
        }

        return false;
    }

    /// <summary>The methods with source in this compilation that <paramref name="method"/>'s body calls (not counting its contracts).</summary>
    private List<IMethodSymbol> CalledSourceMethods(IMethodSymbol method)
    {
        var graph = CallGraphs.GetValue(_compilation, _ => new(SymbolEqualityComparer.Default));
        if (graph.TryGetValue(method, out var known))
        {
            return known;
        }

        var called = new List<IMethodSymbol>();
        foreach (var synRef in method.DeclaringSyntaxReferences)
        {
            var syntax = synRef.GetSyntax();
            if (!_compilation.ContainsSyntaxTree(syntax.SyntaxTree))
            {
                continue;
            }

            var model = _compilation.GetSemanticModel(syntax.SyntaxTree);
            if (model.GetOperation(syntax) is not { } body)
            {
                continue;
            }

            foreach (var op in body.DescendantsAndSelf())
            {
                if (op is IInvocationOperation inv &&
                    inv.TargetMethod.ContainingType?.ToDisplayString() != VerifiableSymbolHelpers.SpecTypeName &&
                    !IsInsideSpecCall(inv) &&
                    inv.TargetMethod.OriginalDefinition is { } target &&
                    !target.DeclaringSyntaxReferences.IsDefaultOrEmpty)
                {
                    called.Add(target);
                }
            }
        }

        return graph.GetOrAdd(method, called);
    }

    private static bool IsInsideSpecCall(IOperation op)
    {
        for (var parent = op.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is IInvocationOperation inv &&
                inv.TargetMethod.ContainingType?.ToDisplayString() == VerifiableSymbolHelpers.SpecTypeName)
            {
                return true;
            }
        }

        return false;
    }

    // ---- foreach over a sequence ----

    /// <summary>The sequence a foreach enumerates, by the capture that holds its enumerator.</summary>
    private readonly Dictionary<CaptureId, (SmtTypedTerm Sequence, int SequenceId, ITypeSymbol ElementType)> _enumerators = new();

    private ITypeSymbol EnumeratorIndexType => _compilation.GetSpecialType(SpecialType.System_UInt32);

    private SmtTypedTerm BindEnumeratorIndex(string term, bool isFree = false)
    {
        // The index is an unbounded integer in every mode (uint has the sort Int), unlike int under [CheckedArithmetic].
        return BindSsaValue("enumIdx", new SmtTypedTerm(term, EnumeratorIndexType), isFree);
    }

    /// <summary>
    /// <c>foreach</c> over an <c>ImmutableArray&lt;T&gt;</c>, <c>ImmutableList&lt;T&gt;</c> or <c>IReadOnlyList&lt;T&gt;</c>
    /// is modelled with the number of items the enumerator has handed out so far. Capturing the enumerator starts it at
    /// zero; <c>MoveNext</c> hands out the next item while there is one; <c>Current</c> is the item before that count.
    /// </summary>
    private bool TryLowerEnumeratorCapture(IFlowCaptureOperation cap, string gamma, BlockEnv env, bool emitObligations, int inlineDepth, out SmtTypedTerm result)
    {
        result = null!;
        if (cap.Value is not IInvocationOperation { TargetMethod.Name: "GetEnumerator", Arguments.Length: 0, Instance: { } instance })
        {
            return false;
        }

        while (instance is IConversionOperation { Conversion.IsIdentity: false, Operand: { } inner } conv &&
               (conv.Conversion.IsReference || conv.Conversion.IsImplicit))
        {
            instance = inner;
        }

        var sequence = LowerExpression(instance, gamma, env, emitObligations, inlineDepth);
        if (!_types.TryGetSequenceInfo(sequence.Type, out _, out var seqId, out var elemType, out var isImmArr))
        {
            return false;
        }

        if (isImmArr)
        {
            EmitImmutableArrayNonDefaultObligation(sequence, $"ImmutableArray must not be default for 'foreach' over '{instance.Syntax}'", instance.Syntax.GetLocation(), gamma);
        }

        _enumerators[cap.Id] = (BindSsaValue("enumSeq", sequence), seqId, elemType!);
        result = BindEnumeratorIndex("0");
        env.Captures[cap.Id] = result;
        env.CaptureAliases.Remove(cap.Id);
        return true;
    }

    private bool TryLowerEnumeratorMoveNext(IInvocationOperation inv, BlockEnv env, out SmtTypedTerm result)
    {
        result = null!;
        if (inv.TargetMethod.Name != "MoveNext" || inv.Arguments.Length != 0 ||
            inv.Instance is not IFlowCaptureReferenceOperation reference ||
            !_enumerators.TryGetValue(reference.Id, out var enumerator) ||
            !env.Captures.TryGetValue(reference.Id, out var index))
        {
            return false;
        }

        var hasNext = BindSsaValue(
            "moveNext",
            new SmtTypedTerm($"(< {index.Term} (seq_count_{enumerator.SequenceId} {enumerator.Sequence.Term}))", _compilation.GetSpecialType(SpecialType.System_Boolean)));
        env.Captures[reference.Id] = BindEnumeratorIndex($"(ite {hasNext.Term} (+ {index.Term} 1) {index.Term})");
        result = hasNext;
        return true;
    }

    private bool TryLowerEnumeratorCurrent(IPropertyReferenceOperation propRef, BlockEnv env, out SmtTypedTerm result)
    {
        result = null!;
        if (propRef.Property.Name != "Current" ||
            propRef.Instance is not IFlowCaptureReferenceOperation reference ||
            !_enumerators.TryGetValue(reference.Id, out var enumerator) ||
            !env.Captures.TryGetValue(reference.Id, out var index))
        {
            return false;
        }

        var item = $"(- {index.Term} 1)";
        var inRange = $"(and (>= {item} 0) (< {item} (seq_count_{enumerator.SequenceId} {enumerator.Sequence.Term})))";
        var element = new SmtTypedTerm($"(select (seq_elems_{enumerator.SequenceId} {enumerator.Sequence.Term}) {item})", enumerator.ElementType);
        EmitOnDemandElementAssumptions(inRange, element, propRef.Syntax.GetLocation());
        result = element;
        return true;
    }

    /// <summary>
    /// At the head of a loop, an enumerator captured before the loop is somewhere between its start and the end of its
    /// sequence, not at the place it had on entry. Returns the assumptions that say so.
    /// </summary>
    private List<string> HavocEnumeratorCaptures(HashSet<BasicBlock> loopBody, BlockEnv preEnv, BlockEnv loopEnv)
    {
        var assumptions = new List<string>();
        if (_enumerators.Count == 0)
        {
            return assumptions;
        }

        var seen = new HashSet<CaptureId>();
        foreach (var block in loopBody.OrderBy(b => b.Ordinal))
        {
            foreach (var op in LoopBlockOperations(block))
            {
                foreach (var reference in op.DescendantsAndSelf().OfType<IFlowCaptureReferenceOperation>())
                {
                    if (!_enumerators.TryGetValue(reference.Id, out var enumerator) ||
                        !preEnv.Captures.ContainsKey(reference.Id) ||
                        !seen.Add(reference.Id))
                    {
                        continue;
                    }

                    var index = BindEnumeratorIndex("0", isFree: true);
                    loopEnv.Captures[reference.Id] = index;
                    assumptions.Add($"(and (>= {index.Term} 0) (<= {index.Term} (seq_count_{enumerator.SequenceId} {enumerator.Sequence.Term})))");
                }
            }
        }

        return assumptions;
    }
}
