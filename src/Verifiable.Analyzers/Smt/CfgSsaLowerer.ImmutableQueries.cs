using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Verifiable.Analyzers.Smt;

internal sealed partial class CfgSsaLowerer
{
    private int _quantifierCounter;
    private int _inQuantifierDepth;
    private List<string>? _quantifierObligations;
    private readonly Dictionary<string, string> _memoizedCountTerms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<SmtTypedTerm>> _knownConcreteSequences = new(StringComparer.Ordinal);

    private sealed record SequencePipeline(
        SmtTypedTerm BaseSeq,
        int SeqId,
        ITypeSymbol BaseElemType,
        string CountTerm,
        ITypeSymbol CurrentElemType,
        Func<string, SmtTypedTerm> ElementAt,
        Func<string, string> FilterAt,
        bool HasFilter);

    private void EmitImmutableArrayNonDefaultObligation(SmtTypedTerm seqTerm, string context, Location loc, string gamma)
    {
        if (_types.TryGetSequenceInfo(seqTerm.Type, out _, out var seqId, out _, out var isImmArr) && isImmArr)
        {
            EmitSafetyObligation("CollectionBounds", context, loc, gamma, $"(not (seq_is_default_{seqId} {seqTerm.Term}))");
        }
    }

    private bool TryLowerLinqInvocation(
        IInvocationOperation inv,
        string gamma,
        BlockEnv env,
        bool emitObligations,
        int inlineDepth,
        out SmtTypedTerm result)
    {
        result = default!;
        var method = inv.TargetMethod;
        var containing = method.ContainingType?.OriginalDefinition.ToDisplayString();
        if (containing is not ("System.Linq.Enumerable" or "System.Linq.ImmutableArrayExtensions"))
        {
            return false;
        }

        var loc = inv.Syntax.GetLocation();
        var boolType = _compilation.GetSpecialType(SpecialType.System_Boolean);
        var intType = _compilation.GetSpecialType(SpecialType.System_Int32);

        switch (method.Name)
        {
            case "Any":
            {
                var sourceArg = GetOrderedArgOp(inv, 0);
                var predArg = inv.Arguments.Length >= 2 ? GetOrderedArgOp(inv, 1) : null;

                // Check for GroupBy(...).Any(...)
                if (TryUnwrapGroupBy(sourceArg, out var groupSource, out var keyLambda))
                {
                    result = LowerGroupByQuantifier(
                        groupSource!,
                        keyLambda!,
                        isAll: false,
                        predArg,
                        gamma,
                        env,
                        emitObligations,
                        inlineDepth,
                        loc);
                    return true;
                }

                if (!TryExtractPipeline(sourceArg, gamma, env, emitObligations, inlineDepth, out var pipe))
                {
                    throw new ContractLoweringException($"Unsupported LINQ source in '{inv.Syntax}'.");
                }

                var idxVar = $"__ver_i_{++_quantifierCounter}";
                var inRange = $"(and (>= {idxVar} 0) (< {idxVar} {pipe!.CountTerm}))";
                var filterCond = pipe.FilterAt(idxVar);

                string predCond;
                if (predArg is not null)
                {
                    if (!TryExtractSingleParamLambda(predArg, out var pParam, out var pBody))
                    {
                        throw new ContractLoweringException($"LINQ 'Any' predicate must be a single-expression lambda in '{inv.Syntax}'.");
                    }

                    var elem = pipe.ElementAt(idxVar);
                    var (pTerm, obligations) = LowerLambdaInQuantifier(pParam!, pBody!, elem, gamma, env, inlineDepth);
                    EmitQuantifiedLambdaObligations(idxVar, CombineAnd(inRange, filterCond), obligations, loc, gamma);
                    predCond = pTerm.Term;
                }
                else
                {
                    predCond = "true";
                }

                var body = CombineAnd(inRange, CombineAnd(filterCond, predCond));
                result = new SmtTypedTerm($"(exists (({idxVar} Int)) {body})", boolType);
                return true;
            }

            case "All":
            {
                var sourceArg = GetOrderedArgOp(inv, 0);
                var predArg = GetOrderedArgOp(inv, 1);

                // Check for GroupBy(...).All(...)
                if (TryUnwrapGroupBy(sourceArg, out var groupSource, out var keyLambda))
                {
                    result = LowerGroupByQuantifier(
                        groupSource!,
                        keyLambda!,
                        isAll: true,
                        predArg,
                        gamma,
                        env,
                        emitObligations,
                        inlineDepth,
                        loc);
                    return true;
                }

                if (!TryExtractPipeline(sourceArg, gamma, env, emitObligations, inlineDepth, out var pipe))
                {
                    throw new ContractLoweringException($"Unsupported LINQ source in '{inv.Syntax}'.");
                }

                if (!TryExtractSingleParamLambda(predArg, out var pParam, out var pBody))
                {
                    throw new ContractLoweringException($"LINQ 'All' predicate must be a single-expression lambda in '{inv.Syntax}'.");
                }

                var idxVar = $"__ver_i_{++_quantifierCounter}";
                var inRange = $"(and (>= {idxVar} 0) (< {idxVar} {pipe!.CountTerm}))";
                var guard = CombineAnd(inRange, pipe.FilterAt(idxVar));
                var elem = pipe.ElementAt(idxVar);
                var (pTerm, obligations) = LowerLambdaInQuantifier(pParam!, pBody!, elem, gamma, env, inlineDepth);
                EmitQuantifiedLambdaObligations(idxVar, guard, obligations, loc, gamma);

                result = new SmtTypedTerm($"(forall (({idxVar} Int)) (=> {guard} {pTerm.Term}))", boolType);
                return true;
            }

            case "Count":
            {
                var sourceArg = GetOrderedArgOp(inv, 0);
                var predArg = inv.Arguments.Length >= 2 ? GetOrderedArgOp(inv, 1) : null;

                if (!TryExtractPipeline(sourceArg, gamma, env, emitObligations, inlineDepth, out var pipe))
                {
                    throw new ContractLoweringException($"Unsupported LINQ source in '{inv.Syntax}'.");
                }

                // Unfiltered Count() is just the sequence length (quantifier-free!)
                if (!pipe!.HasFilter && predArg is null)
                {
                    result = _translator.ConvertTo(new SmtTypedTerm(pipe.CountTerm, intType), intType);
                    return true;
                }

                IParameterSymbol? pParam = null;
                IOperation? pBody = null;
                if (predArg is not null && !TryExtractSingleParamLambda(predArg, out pParam, out pBody))
                {
                    throw new ContractLoweringException($"LINQ 'Count' predicate must be a single-expression lambda in '{inv.Syntax}'.");
                }

                // If the base sequence is a concrete known literal sequence, unroll exact count!
                if (_knownConcreteSequences.TryGetValue(pipe.BaseSeq.Term, out var concreteElems))
                {
                    if (concreteElems.Count == 0)
                    {
                        result = new SmtTypedTerm(_translator.FormatIntLiteral(0, 32), intType);
                        return true;
                    }

                    var sumTerms = new List<string>(concreteElems.Count);
                    for (var i = 0; i < concreteElems.Count; i++)
                    {
                        var idxLit = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        var fCond = pipe.FilterAt(idxLit);
                        var elemAtI = pipe.ElementAt(idxLit);
                        var totalCond = fCond;
                        if (pParam is not null)
                        {
                            var lambdaEnv = env.Clone();
                            lambdaEnv.Vars[pParam] = elemAtI;
                            var pRes = LowerExpression(pBody!, CombineAnd(gamma, fCond), lambdaEnv, emitObligations, inlineDepth + 1);
                            totalCond = CombineAnd(totalCond, pRes.Term);
                        }
                        sumTerms.Add($"(ite {totalCond} 1 0)");
                    }

                    var totalSum = sumTerms.Count == 1 ? sumTerms[0] : $"(+ {string.Join(" ", sumTerms)})";
                    result = _translator.ConvertTo(new SmtTypedTerm(totalSum, intType), intType);
                    return true;
                }

                if (_inQuantifierDepth > 0)
                {
                    throw new ContractLoweringException("Filtered LINQ Count() inside a nested quantifier is not supported.");
                }

                // Canonical index for memoization so xs.Where(p).Count() == xs.Count(p) maps to the exact same symbol!
                const string canonIdx = "__ver_cnt_i";
                var inRange = $"(and (>= {canonIdx} 0) (< {canonIdx} {pipe.CountTerm}))";
                var filterCond = pipe.FilterAt(canonIdx);
                var elemCanon = pipe.ElementAt(canonIdx);

                string fullPred;
                if (pParam is not null)
                {
                    var (pTerm, obligations) = LowerLambdaInQuantifier(pParam, pBody!, elemCanon, gamma, env, inlineDepth);
                    EmitQuantifiedLambdaObligations(canonIdx, CombineAnd(inRange, filterCond), obligations, loc, gamma);
                    fullPred = CombineAnd(filterCond, pTerm.Term);
                }
                else
                {
                    fullPred = filterCond;
                }

                var key = $"{pipe.BaseSeq.Term}|{fullPred}";
                if (!_memoizedCountTerms.TryGetValue(key, out var cntName))
                {
                    cntName = $"linq_cnt_{++_ssaCounter}";
                    _memoizedCountTerms[key] = cntName;
                    _decls.Add($"(declare-const {cntName} Int)");
                    AddAssertion($"(and (>= {cntName} 0) (<= {cntName} {pipe.CountTerm}))", "LinqCountAxiom", "Count bounds", loc);
                    AddAssertion(
                        $"(= (= {cntName} 0) (forall (({canonIdx} Int)) (=> {inRange} (not {fullPred}))))",
                        "LinqCountAxiom", "Count == 0 iff no matching element", loc);
                    AddAssertion(
                        $"(= (= {cntName} {pipe.CountTerm}) (forall (({canonIdx} Int)) (=> {inRange} {fullPred})))",
                        "LinqCountAxiom", "Count == Length iff all elements match", loc);
                }

                result = _translator.ConvertTo(new SmtTypedTerm(cntName, intType), intType);
                return true;
            }

            case "Where":
            case "Select":
            case "GroupBy":
                throw new ContractLoweringException(
                    $"Intermediate LINQ operator '{method.Name}' must be consumed by Any, All, or Count in '{inv.Syntax}'.");

            default:
                return false;
        }
    }

    private bool TryExtractPipeline(
        IOperation op,
        string gamma,
        BlockEnv env,
        bool emitObligations,
        int inlineDepth,
        out SequencePipeline? pipeline)
    {
        pipeline = null;
        var cur = op;
        while (cur is IConversionOperation conv)
        {
            cur = conv.Operand;
        }

        if (cur is IInvocationOperation inv &&
            inv.TargetMethod.ContainingType?.OriginalDefinition.ToDisplayString() is
                "System.Linq.Enumerable" or "System.Linq.ImmutableArrayExtensions")
        {
            var name = inv.TargetMethod.Name;
            if (name == "Where" && inv.Arguments.Length == 2)
            {
                if (!TryExtractPipeline(GetOrderedArgOp(inv, 0), gamma, env, emitObligations, inlineDepth, out var inner))
                {
                    return false;
                }

                if (!TryExtractSingleParamLambda(GetOrderedArgOp(inv, 1), out var pParam, out var pBody))
                {
                    throw new ContractLoweringException($"LINQ 'Where' predicate must be a single-expression lambda in '{inv.Syntax}'.");
                }

                var loc = inv.Syntax.GetLocation();
                var capturedInner = inner!;
                var capturedParam = pParam!;
                var capturedBody = pBody!;
                var capturedEnv = env.Clone();

                // Verify safety obligations of the Where predicate once for all valid indices
                var checkIdx = $"__ver_w_{++_quantifierCounter}";
                var inRange = $"(and (>= {checkIdx} 0) (< {checkIdx} {capturedInner.CountTerm}))";
                var (_, obs) = LowerLambdaInQuantifier(capturedParam, capturedBody, capturedInner.ElementAt(checkIdx), gamma, capturedEnv, inlineDepth);
                EmitQuantifiedLambdaObligations(checkIdx, CombineAnd(inRange, capturedInner.FilterAt(checkIdx)), obs, loc, gamma);

                pipeline = capturedInner with
                {
                    HasFilter = true,
                    FilterAt = idx =>
                    {
                        var prevF = capturedInner.FilterAt(idx);
                        var (pTerm, _) = LowerLambdaInQuantifier(capturedParam, capturedBody, capturedInner.ElementAt(idx), gamma, capturedEnv, inlineDepth);
                        return CombineAnd(prevF, pTerm.Term);
                    }
                };
                return true;
            }

            if (name == "Select" && inv.Arguments.Length == 2)
            {
                if (!TryExtractPipeline(GetOrderedArgOp(inv, 0), gamma, env, emitObligations, inlineDepth, out var inner))
                {
                    return false;
                }

                if (!TryExtractSingleParamLambda(GetOrderedArgOp(inv, 1), out var pParam, out var pBody))
                {
                    throw new ContractLoweringException($"LINQ 'Select' selector must be a single-expression lambda in '{inv.Syntax}'.");
                }

                var loc = inv.Syntax.GetLocation();
                var capturedInner = inner!;
                var capturedParam = pParam!;
                var capturedBody = pBody!;
                var capturedEnv = env.Clone();

                // Verify safety obligations of the Select selector once for all valid filtered indices
                var checkIdx = $"__ver_s_{++_quantifierCounter}";
                var inRange = $"(and (>= {checkIdx} 0) (< {checkIdx} {capturedInner.CountTerm}))";
                var (sampleProj, obs) = LowerLambdaInQuantifier(capturedParam, capturedBody, capturedInner.ElementAt(checkIdx), gamma, capturedEnv, inlineDepth);
                EmitQuantifiedLambdaObligations(checkIdx, CombineAnd(inRange, capturedInner.FilterAt(checkIdx)), obs, loc, gamma);

                pipeline = capturedInner with
                {
                    CurrentElemType = sampleProj.Type,
                    ElementAt = idx =>
                    {
                        var (proj, _) = LowerLambdaInQuantifier(capturedParam, capturedBody, capturedInner.ElementAt(idx), gamma, capturedEnv, inlineDepth);
                        return proj;
                    }
                };
                return true;
            }

            return false;
        }

        if (cur.Type is not null && _types.TryGetSequenceInfo(cur.Type, out _, out var seqId, out var elemType, out _))
        {
            var baseSeq = LowerExpression(cur, gamma, env, emitObligations, inlineDepth);
            EmitImmutableArrayNonDefaultObligation(baseSeq, $"ImmutableArray must not be default before LINQ query '{op.Syntax}'", op.Syntax.GetLocation(), gamma);
            var countTerm = $"(seq_count_{seqId} {baseSeq.Term})";
            pipeline = new SequencePipeline(
                baseSeq,
                seqId,
                elemType!,
                countTerm,
                elemType!,
                idx => new SmtTypedTerm($"(select (seq_elems_{seqId} {baseSeq.Term}) {idx})", elemType!),
                _ => "true",
                HasFilter: false);
            return true;
        }

        return false;
    }

    private static bool TryUnwrapGroupBy(IOperation op, out IOperation? source, out IOperation? keySelector)
    {
        source = null;
        keySelector = null;
        var cur = op;
        while (cur is IConversionOperation conv)
        {
            cur = conv.Operand;
        }

        if (cur is IInvocationOperation inv &&
            inv.TargetMethod.ContainingType?.OriginalDefinition.ToDisplayString() == "System.Linq.Enumerable" &&
            inv.TargetMethod.Name == "GroupBy" &&
            inv.Arguments.Length == 2)
        {
            source = GetOrderedArgOp(inv, 0);
            keySelector = GetOrderedArgOp(inv, 1);
            return true;
        }

        return false;
    }

    private enum GroupCountPredicateKind
    {
        None = 0,
        CountGreaterThanOne,    // g.Count() > 1, g.Count() >= 2, g.Count() != 1
        CountEqualsOne          // g.Count() == 1, g.Count() <= 1, g.Count() < 2
    }

    private SmtTypedTerm LowerGroupByQuantifier(
        IOperation groupSource,
        IOperation keySelectorOp,
        bool isAll,
        IOperation? groupPredOp,
        string gamma,
        BlockEnv env,
        bool emitObligations,
        int inlineDepth,
        Location loc)
    {
        var boolType = _compilation.GetSpecialType(SpecialType.System_Boolean);

        if (!TryExtractPipeline(groupSource, gamma, env, emitObligations, inlineDepth, out var pipe))
        {
            throw new ContractLoweringException($"Unsupported GroupBy source in '{groupSource.Syntax}'.");
        }

        if (!TryExtractSingleParamLambda(keySelectorOp, out var kParam, out var kBody))
        {
            throw new ContractLoweringException("GroupBy key selector must be a single-expression lambda.");
        }

        // Verify key selector obligations and check key type supports value equality
        var probeIdx = $"__ver_gk_{++_quantifierCounter}";
        var probeInRange = $"(and (>= {probeIdx} 0) (< {probeIdx} {pipe!.CountTerm}))";
        var (sampleKey, keyObs) = LowerLambdaInQuantifier(kParam!, kBody!, pipe.ElementAt(probeIdx), gamma, env, inlineDepth);
        EmitQuantifiedLambdaObligations(probeIdx, CombineAnd(probeInRange, pipe.FilterAt(probeIdx)), keyObs, loc, gamma);

        if (InitializerProjection.UsesReferenceEquality(sampleKey.Type))
        {
            throw new ContractLoweringException(
                $"GroupBy key type '{sampleKey.Type.Name}' uses reference equality and is not supported in SMT.");
        }

        // GroupBy(...).Any() with no predicate <=> source has at least one matching element
        if (!isAll && groupPredOp is null)
        {
            var idx = $"__ver_i_{++_quantifierCounter}";
            var cond = CombineAnd($"(and (>= {idx} 0) (< {idx} {pipe.CountTerm}))", pipe.FilterAt(idx));
            return new SmtTypedTerm($"(exists (({idx} Int)) {cond})", boolType);
        }

        if (groupPredOp is null || !TryExtractSingleParamLambda(groupPredOp, out var gParam, out var gBody))
        {
            throw new ContractLoweringException("GroupBy continuation predicate must be a single-expression lambda.");
        }

        var predKind = ClassifyGroupCountPredicate(gParam!, gBody!);
        if (predKind == GroupCountPredicateKind.None)
        {
            throw new ContractLoweringException(
                $"Unsupported GroupBy predicate '{gBody!.Syntax}'; supported predicates check group uniqueness (e.g. g => g.Count() > 1 or g => g.Count() == 1).");
        }

        var iVar = $"__ver_gi_{++_quantifierCounter}";
        var jVar = $"__ver_gj_{++_quantifierCounter}";
        var fI = pipe.FilterAt(iVar);
        var fJ = pipe.FilterAt(jVar);
        var (kI, _) = LowerLambdaInQuantifier(kParam!, kBody!, pipe.ElementAt(iVar), gamma, env, inlineDepth);
        var (kJ, _) = LowerLambdaInQuantifier(kParam!, kBody!, pipe.ElementAt(jVar), gamma, env, inlineDepth);
        var (kl, kr, _) = _translator.UnifyForComparison(kI, kJ);
        var keyEq = $"(= {kl} {kr})";

        string formula;
        if (!isAll && predKind == GroupCountPredicateKind.CountGreaterThanOne)
        {
            // Some group has > 1 element <=> exists distinct filtered i < j with same key
            var body = CombineAnd(
                $"(and (>= {iVar} 0) (< {iVar} {pipe.CountTerm}) (>= {jVar} 0) (< {jVar} {pipe.CountTerm}) (< {iVar} {jVar}))",
                CombineAnd(fI, CombineAnd(fJ, keyEq)));
            formula = $"(exists (({iVar} Int) ({jVar} Int)) {body})";
        }
        else if (isAll && predKind == GroupCountPredicateKind.CountEqualsOne)
        {
            // All groups have == 1 element <=> forall distinct filtered i < j, keys differ
            var ant = CombineAnd(
                $"(and (>= {iVar} 0) (< {iVar} {pipe.CountTerm}) (>= {jVar} 0) (< {jVar} {pipe.CountTerm}) (< {iVar} {jVar}))",
                CombineAnd(fI, fJ));
            formula = $"(forall (({iVar} Int) ({jVar} Int)) (=> {ant} (not {keyEq})))";
        }
        else if (isAll && predKind == GroupCountPredicateKind.CountGreaterThanOne)
        {
            // Every group has > 1 element <=> every filtered i has some other filtered j != i with same key
            var antI = CombineAnd($"(and (>= {iVar} 0) (< {iVar} {pipe.CountTerm}))", fI);
            var exJ = CombineAnd($"(and (>= {jVar} 0) (< {jVar} {pipe.CountTerm}) (not (= {jVar} {iVar})))", CombineAnd(fJ, keyEq));
            formula = $"(forall (({iVar} Int)) (=> {antI} (exists (({jVar} Int)) {exJ})))";
        }
        else
        {
            // Any(g => g.Count() == 1) <=> exists filtered i with no other filtered j != i sharing its key
            var antI = CombineAnd($"(and (>= {iVar} 0) (< {iVar} {pipe.CountTerm}))", fI);
            var antJ = CombineAnd($"(and (>= {jVar} 0) (< {jVar} {pipe.CountTerm}) (not (= {jVar} {iVar})))", fJ);
            formula = $"(exists (({iVar} Int)) (and {antI} (forall (({jVar} Int)) (=> {antJ} (not {keyEq})))))";
        }

        return new SmtTypedTerm(formula, boolType);
    }

    private static GroupCountPredicateKind ClassifyGroupCountPredicate(IParameterSymbol groupParam, IOperation body)
    {
        var cur = body;
        while (cur is IParenthesizedOperation p) cur = p.Operand;

        if (cur is IUnaryOperation { OperatorKind: UnaryOperatorKind.Not } neg)
        {
            var inner = ClassifyGroupCountPredicate(groupParam, neg.Operand);
            return inner switch
            {
                GroupCountPredicateKind.CountGreaterThanOne => GroupCountPredicateKind.CountEqualsOne,
                GroupCountPredicateKind.CountEqualsOne => GroupCountPredicateKind.CountGreaterThanOne,
                _ => GroupCountPredicateKind.None
            };
        }

        if (cur is not IBinaryOperation bin)
        {
            return GroupCountPredicateKind.None;
        }

        var leftIsCount = IsGroupCountCall(groupParam, bin.LeftOperand);
        var rightIsCount = IsGroupCountCall(groupParam, bin.RightOperand);
        if (leftIsCount == rightIsCount)
        {
            return GroupCountPredicateKind.None;
        }

        var constOp = leftIsCount ? bin.RightOperand : bin.LeftOperand;
        if (!TryGetConstantInt(constOp, out var k))
        {
            return GroupCountPredicateKind.None;
        }

        var opKind = bin.OperatorKind;
        if (rightIsCount)
        {
            opKind = opKind switch
            {
                BinaryOperatorKind.LessThan => BinaryOperatorKind.GreaterThan,
                BinaryOperatorKind.LessThanOrEqual => BinaryOperatorKind.GreaterThanOrEqual,
                BinaryOperatorKind.GreaterThan => BinaryOperatorKind.LessThan,
                BinaryOperatorKind.GreaterThanOrEqual => BinaryOperatorKind.LessThanOrEqual,
                _ => opKind
            };
        }

        // Every GroupBy group is non-empty (Count >= 1).
        return (opKind, k) switch
        {
            (BinaryOperatorKind.GreaterThan, 1) => GroupCountPredicateKind.CountGreaterThanOne,
            (BinaryOperatorKind.GreaterThanOrEqual, 2) => GroupCountPredicateKind.CountGreaterThanOne,
            (BinaryOperatorKind.NotEquals, 1) => GroupCountPredicateKind.CountGreaterThanOne,
            (BinaryOperatorKind.Equals, 1) => GroupCountPredicateKind.CountEqualsOne,
            (BinaryOperatorKind.LessThanOrEqual, 1) => GroupCountPredicateKind.CountEqualsOne,
            (BinaryOperatorKind.LessThan, 2) => GroupCountPredicateKind.CountEqualsOne,
            _ => GroupCountPredicateKind.None
        };
    }

    private static bool IsGroupCountCall(IParameterSymbol groupParam, IOperation op)
    {
        var cur = op;
        while (cur is IConversionOperation or IParenthesizedOperation)
        {
            cur = cur is IConversionOperation c ? c.Operand : ((IParenthesizedOperation)cur).Operand;
        }

        if (cur is IInvocationOperation inv &&
            inv.TargetMethod.ContainingType?.OriginalDefinition.ToDisplayString() == "System.Linq.Enumerable" &&
            inv.TargetMethod.Name == "Count" &&
            inv.Arguments.Length == 1)
        {
            var arg = inv.Arguments[0].Value;
            while (arg is IConversionOperation c) arg = c.Operand;
            return arg is IParameterReferenceOperation pr &&
                   SymbolEqualityComparer.Default.Equals(pr.Parameter, groupParam);
        }

        return false;
    }

    private (SmtTypedTerm Result, List<string> Obligations) LowerLambdaInQuantifier(
        IParameterSymbol param,
        IOperation body,
        SmtTypedTerm argTerm,
        string gamma,
        BlockEnv outerEnv,
        int inlineDepth)
    {
        var lambdaEnv = outerEnv.Clone();
        lambdaEnv.Vars[param] = argTerm;

        var prevObs = _quantifierObligations;
        var collected = new List<string>();
        _quantifierObligations = collected;
        _inQuantifierDepth++;
        try
        {
            var res = LowerExpression(body, "true", lambdaEnv, emitObligations: false, inlineDepth + 1);
            return (res, collected);
        }
        finally
        {
            _inQuantifierDepth--;
            _quantifierObligations = prevObs;
        }
    }

    private void EmitQuantifiedLambdaObligations(
        string idxVar,
        string guard,
        List<string> obligations,
        Location loc,
        string gamma,
        string idxSort = "Int")
    {
        foreach (var ob in obligations)
        {
            EmitSafetyObligation(
                "Precondition",
                "Quantified lambda safety condition",
                loc,
                gamma,
                $"(forall (({idxVar} {idxSort})) (=> {guard} {ob}))");
        }
    }

    private SmtTypedTerm LowerSpecQuantifier(
        IInvocationOperation inv,
        bool isForAll,
        string gamma,
        BlockEnv env,
        bool emitObligations,
        int inlineDepth)
    {
        var loc = inv.Syntax.GetLocation();
        var boolType = _compilation.GetSpecialType(SpecialType.System_Boolean);
        var intType = _compilation.GetSpecialType(SpecialType.System_Int32);

        var startTerm = LowerExpression(GetOrderedArgOp(inv, 0), gamma, env, emitObligations, inlineDepth);
        var endTerm = LowerExpression(GetOrderedArgOp(inv, 1), gamma, env, emitObligations, inlineDepth);
        var predArg = GetOrderedArgOp(inv, 2);

        if (!TryExtractSingleParamLambda(predArg, out var idxParam, out var predBody))
        {
            var fnName = isForAll ? "Spec.ForAll" : "Spec.Exists";
            throw new ContractLoweringException($"{fnName} predicate must be a single-expression lambda in '{inv.Syntax}'.");
        }

        var idxVar = $"__ver_q_{++_quantifierCounter}";
        string idxSort;
        string inRange;
        if (_types.UseBitVec)
        {
            idxSort = "(_ BitVec 32)";
            var startBv = _translator.ConvertTo(startTerm, intType).Term;
            var endBv = _translator.ConvertTo(endTerm, intType).Term;
            inRange = $"(and (bvsge {idxVar} {startBv}) (bvslt {idxVar} {endBv}))";
        }
        else
        {
            idxSort = "Int";
            var startInt = _translator.IntTermForIndex(startTerm);
            var endInt = _translator.IntTermForIndex(endTerm);
            inRange = $"(and (>= {idxVar} {startInt}) (< {idxVar} {endInt}))";
        }

        var idxTyped = new SmtTypedTerm(idxVar, intType);
        var (pTerm, obligations) = LowerLambdaInQuantifier(idxParam!, predBody!, idxTyped, CombineAnd(gamma, inRange), env, inlineDepth);
        if (ChecksThrowingOperations(emitObligations))
        {
            EmitQuantifiedLambdaObligations(idxVar, inRange, obligations, loc, gamma, idxSort);
        }

        var quantified = isForAll
            ? $"(forall (({idxVar} {idxSort})) (=> {inRange} {pTerm.Term}))"
            : $"(exists (({idxVar} {idxSort})) (and {inRange} {pTerm.Term}))";
        return new SmtTypedTerm(quantified, boolType);
    }

    private bool TryExtractSingleParamLambda(
        IOperation argOp,
        out IParameterSymbol? param,
        out IOperation? bodyExpr)
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

        if (cur is IFlowAnonymousFunctionOperation flowAnon &&
            _compilation.SyntaxTrees.Contains(flowAnon.Syntax.SyntaxTree))
        {
            var model = _compilation.GetSemanticModel(flowAnon.Syntax.SyntaxTree);
            cur = model.GetOperation(flowAnon.Syntax) ?? cur;
        }

        if (cur is IAnonymousFunctionOperation anon &&
            anon.Symbol.Parameters.Length == 1 &&
            anon.Body is { Operations.Length: 1 } block &&
            block.Operations[0] is IReturnOperation { ReturnedValue: { } retExpr })
        {
            param = anon.Symbol.Parameters[0];
            bodyExpr = retExpr;
            return true;
        }

        param = null;
        bodyExpr = null;
        return false;
    }

    private static bool TryGetConstantInt(IOperation op, out int value)
    {
        value = 0;
        var cur = op;
        while (cur is IConversionOperation or IParenthesizedOperation)
        {
            cur = cur is IConversionOperation c ? c.Operand : ((IParenthesizedOperation)cur).Operand;
        }

        if (cur.ConstantValue.HasValue && cur.ConstantValue.Value is not null)
        {
            try
            {
                value = Convert.ToInt32(cur.ConstantValue.Value, System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        return false;
    }
}
