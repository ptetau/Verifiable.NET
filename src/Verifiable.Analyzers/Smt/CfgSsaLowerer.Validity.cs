using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Verifiable.Analyzers.Smt;

/// <summary>
/// Validity rules (TypeAlias <c>IsValid</c>, <c>[Invariant]</c>, closed enums) are assumed where a value
/// enters a method (parameter, call result, element, field). They are proved here where a value
/// leaves into such a boundary: a call argument, a returned <c>T?</c> or array, a constructor's
/// <c>Spec.Requires</c>.
/// </summary>
internal sealed partial class CfgSsaLowerer
{
    /// <summary>When set, validity obligations are reported as preconditions of a call argument.</summary>
    private string? _validityBoundary;

    /// <summary>While non-empty, assertions are wrapped as universally quantified over these Int variables.</summary>
    private readonly List<string> _assertionBinders = new();

    /// <summary>The element term of the innermost binder: it mentions every binder, so it triggers instantiation.</summary>
    private readonly List<string> _assertionPatterns = new();

    /// <summary>
    /// Quantified "every element is valid" assumptions for sequences that entered from outside. They are kept out of
    /// the shared assertions (a quantifier there makes every premise check harder) and added only to the proofs of
    /// element-validity obligations, which are the only ones that need them.
    /// </summary>
    private readonly List<(string Term, string Assertion)> _deferredSequenceAssumptions = new();

    /// <summary>Bounds the nesting of arrays inside arrays (or inside recursive records) that is quantified over.</summary>
    private int _validityNesting;
    private const int MaxValidityNesting = 3;

    /// <summary>True when a value of this type is assumed to satisfy a validity rule somewhere inside it.</summary>
    private bool HasValidityRule(ITypeSymbol type, HashSet<ITypeSymbol>? visited = null)
    {
        visited ??= new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        if (!visited.Add(type)) return false;

        if (SmtTypeSystem.HasAnyInvariants(type) || HasAnyClosedEnum(type)) return true;

        if (_types.TryGetNullableInfo(type, out _, out _, out var inner))
        {
            return HasValidityRule(inner!, visited);
        }

        if (_types.TryGetSequenceInfo(type, out _, out _, out var elem, out _))
        {
            return HasValidityRule(elem!, visited);
        }

        if (type is INamedTypeSymbol named &&
            named.OriginalDefinition.ToDisplayString() == "Verifiable.Contracts.Result<T, TError>")
        {
            return HasValidityRule(named.TypeArguments[0], visited);
        }

        return false;
    }

    /// <summary>
    /// Nullable payloads, sequence elements and Result payloads. Returns true when the value was handled here
    /// and the caller has nothing more to check.
    /// </summary>
    private bool TryEmitCompoundValidity(SmtTypedTerm value, string gamma, Location loc)
    {
        if (_types.TryGetNullableInfo(value.Type, out _, out var optId, out var inner) &&
            !IsClosedEnum(inner!, out _, out _))
        {
            if (HasValidityRule(inner!))
            {
                EmitValueInvariantObligations(
                    new SmtTypedTerm($"(opt-val-{optId} {value.Term})", inner!),
                    CombineAnd(gamma, $"(not (= {value.Term} opt-none-{optId}))"),
                    loc);
            }
            return true;
        }

        if (_types.TryGetSequenceInfo(value.Type, out _, out var seqId, out var elemType, out _))
        {
            if (HasValidityRule(elemType!))
            {
                EmitSequenceElementValidity(value, seqId, elemType!, gamma, loc);
            }
            return true;
        }

        if (value.Type is INamedTypeSymbol named &&
            named.OriginalDefinition.ToDisplayString() == "Verifiable.Contracts.Result<T, TError>" &&
            _types.TryGetComposite(named, out var resComp))
        {
            if (HasValidityRule(named.TypeArguments[0]))
            {
                var isSuccess = $"({resComp!.Fields.First(f => f.Name == "IsSuccess").AccessorName} {value.Term})";
                var valField = resComp.Fields.First(f => f.Name == "Value");
                EmitValueInvariantObligations(
                    new SmtTypedTerm($"({valField.AccessorName} {value.Term})", valField.Type),
                    CombineAnd(gamma, isSuccess),
                    loc);
            }
            return true;
        }

        return false;
    }

    private void EmitSequenceElementValidity(SmtTypedTerm seq, int seqId, ITypeSymbol elemType, string gamma, Location loc)
    {
        if (_validityNesting >= MaxValidityNesting) return;
        var idx = $"__ver_vi_{++_quantifierCounter}";
        var elem = new SmtTypedTerm($"(select (seq_elems_{seqId} {seq.Term}) {idx})", elemType);
        var inRange = $"(and (>= {idx} 0) (< {idx} (seq_count_{seqId} {seq.Term})))";

        var collected = new List<string>();
        var prevObligations = _quantifierObligations;
        _quantifierObligations = collected;
        _inQuantifierDepth++;
        _validityNesting++;
        try
        {
            EmitValueInvariantObligations(elem, "true", loc);
        }
        finally
        {
            _validityNesting--;
            _inQuantifierDepth--;
            _quantifierObligations = prevObligations;
        }

        if (collected.Count == 0) return;

        var body = collected.Count == 1 ? collected[0] : "(and " + string.Join(" ", collected) + ")";
        var obligationCount = _obligations.Count;
        AddValidityObligation(
            "TypeInvariant",
            $"every element of the {seq.Type.Name} is valid",
            loc,
            gamma,
            $"(forall (({idx} Int)) (=> {inRange} {body}))",
            $"Every element of a {seq.Type.Name} must satisfy the validity rules of its element type, because callers assume them.");

        if (_obligations.Count != obligationCount)
        {
            var added = _obligations[_obligations.Count - 1];
            var extra = _deferredSequenceAssumptions
                .Where(d => added.ScriptPreamble.Contains(d.Term, StringComparison.Ordinal) ||
                            added.GoalTerm.Contains(d.Term, StringComparison.Ordinal))
                .Select(d => d.Assertion)
                .ToList();
            if (extra.Count > 0)
            {
                _obligations[_obligations.Count - 1] = added with { ScriptPreamble = added.ScriptPreamble + "\n" + string.Join("\n", extra) };
            }
        }
    }

    /// <summary>A nullable element or field present in a valid value carries a valid payload.</summary>
    private bool TryEmitNullableOnDemandAssumptions(string guardCond, SmtTypedTerm element, Location loc)
    {
        if (!_types.TryGetNullableInfo(element.Type, out _, out var optId, out var inner)) return false;
        if (HasValidityRule(inner!))
        {
            EmitOnDemandElementAssumptions(
                CombineAnd(guardCond, $"(not (= {element.Term} opt-none-{optId}))"),
                new SmtTypedTerm($"(opt-val-{optId} {element.Term})", inner!),
                loc);
        }
        return true;
    }

    /// <summary>
    /// Mirror of the element assumptions: every element of a sequence that enters from outside (parameter, call
    /// result, field) is valid. Emitted as one quantified assertion, so that the same fact can be proved on exit.
    /// </summary>
    private void EmitSequenceElementAssumptions(SmtTypedTerm seq, int seqId, ITypeSymbol elemType, string? guardCond, Location loc)
    {
        if (!HasValidityRule(elemType) || _validityNesting >= MaxValidityNesting) return;

        var idx = $"__ver_va_{++_quantifierCounter}";
        var elem = new SmtTypedTerm($"(select (seq_elems_{seqId} {seq.Term}) {idx})", elemType);
        var inRange = $"(and (>= {idx} 0) (< {idx} (seq_count_{seqId} {seq.Term})))";
        var guard = guardCond is null ? inRange : CombineAnd(guardCond, inRange);

        var outermost = _assertionBinders.Count == 0;
        var assertionMark = _assertions.Count;
        var declMark = _decls.Count;
        var premiseMark = _premises.Count;
        var errorMark = _errors.Count;
        var prevObligations = _quantifierObligations;
        _quantifierObligations = new List<string>();
        _assertionBinders.Add(idx);
        _assertionPatterns.Add(elem.Term);
        _inQuantifierDepth++;
        _validityNesting++;
        try
        {
            EmitOnDemandElementAssumptions(guard, elem, loc);
        }
        finally
        {
            _validityNesting--;
            _inQuantifierDepth--;
            _assertionBinders.RemoveAt(_assertionBinders.Count - 1);
            _assertionPatterns.RemoveAt(_assertionPatterns.Count - 1);
            _quantifierObligations = prevObligations;
        }

        // Under the binder no fresh constants can be defined; if the predicate needed one, keep only the
        // on-demand assumption made at each indexing.
        if (_errors.Count != errorMark)
        {
            _errors.RemoveRange(errorMark, _errors.Count - errorMark);
            _assertions.RemoveRange(assertionMark, _assertions.Count - assertionMark);
            _decls.RemoveRange(declMark, _decls.Count - declMark);
            _premises.RemoveRange(premiseMark, _premises.Count - premiseMark);
        }
        else if (outermost)
        {
            for (var i = assertionMark; i < _assertions.Count; i++)
            {
                _deferredSequenceAssumptions.Add((seq.Term, _assertions[i]));
            }
            _assertions.RemoveRange(assertionMark, _assertions.Count - assertionMark);
            _premises.RemoveRange(premiseMark, _premises.Count - premiseMark);
        }
    }

    /// <summary>Same as <c>AddObligation</c>, but reports as a precondition when a call boundary is being checked.</summary>
    private void AddValidityObligation(
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
        if (_inQuantifierDepth > 0 && _quantifierObligations is not null)
        {
            _quantifierObligations.Add(gamma == "true" ? goalTerm : $"(=> {gamma} {goalTerm})");
            return;
        }

        if (_validityBoundary is not null)
        {
            kind = "Precondition";
            description = $"{description} ({_validityBoundary})";
        }

        AddObligation(kind, description, location, gamma, goalTerm, reason,
            contractLocation, contractLocationLabel, resultSmtTerm, goalClauses);
    }

    private static bool IsFrameworkMethod(IMethodSymbol method)
    {
        if (method.ContainingAssembly is null) return false;
        var ns = method.ContainingType?.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        return method.DeclaringSyntaxReferences.Length == 0 &&
            (ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal) ||
             ns == "Microsoft" || ns.StartsWith("Microsoft.", StringComparison.Ordinal));
    }

    /// <summary>The callee assumes each parameter satisfies its type's validity rules, so the argument must.</summary>
    private void EmitArgumentValidityObligations(IMethodSymbol method, IReadOnlyList<SmtTypedTerm> args, string gamma, Location loc)
    {
        if (IsFrameworkMethod(method)) return;

        for (var i = 0; i < method.Parameters.Length && i < args.Count; i++)
        {
            var p = method.Parameters[i];
            if (p.RefKind == RefKind.Out) continue;
            // A bare closed enum parameter is checked by the caller of this method.
            if (IsClosedEnum(p.Type, out _, out _)) continue;
            if (!HasValidityRule(args[i].Type)) continue;

            var prev = _validityBoundary;
            _validityBoundary = $"argument '{p.Name}' of '{method.ContainingType?.Name}.{method.Name}'";
            try
            {
                EmitValueInvariantObligations(args[i], gamma, loc);
            }
            finally
            {
                _validityBoundary = prev;
            }
        }
    }

    /// <summary>
    /// A constructor call is a call: its <c>Spec.Requires</c> must hold, and an explicit constructor body
    /// assumes its parameters are valid like any method.
    /// </summary>
    private void CheckConstructorCall(IObjectCreationOperation objCreate, IReadOnlyList<SmtTypedTerm?> argsByOrdinal, string gamma)
    {
        var ctor = objCreate.Constructor;
        if (ctor is null || !HasExplicitConstructorBody(ctor)) return;
        if (argsByOrdinal.Any(a => a is null)) return;

        var args = argsByOrdinal.Select(a => a!).ToList();
        var loc = objCreate.Syntax.GetLocation();
        EmitArgumentValidityObligations(ctor, args, gamma, loc);
        CheckMethodRequires(ctor, null, args, gamma, loc);
    }

    private static bool HasExplicitConstructorBody(IMethodSymbol ctor) =>
        ctor.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is ConstructorDeclarationSyntax);
}
