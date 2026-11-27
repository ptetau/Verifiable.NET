using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Verifiable.Analyzers.Smt;

internal sealed partial class CfgSsaLowerer
{
    private readonly Dictionary<string, string> _memoizedRoundTerms = new(StringComparer.Ordinal);

    private bool TryLowerDecimalRounding(
        IInvocationOperation inv,
        IReadOnlyList<SmtTypedTerm> argValues,
        Location loc,
        out SmtTypedTerm result)
    {
        result = default!;
        var method = inv.TargetMethod;
        var containing = method.ContainingType;
        if (containing is null) return false;

        var isDecimalOrMath = containing.SpecialType == SpecialType.System_Decimal ||
                              containing.OriginalDefinition.ToDisplayString() == "System.Math";
        if (!isDecimalOrMath || method.ReturnType.SpecialType != SpecialType.System_Decimal)
        {
            return false;
        }

        int decimals = 0;
        int mode = CalendarArithmetic.MidpointToEven;

        switch (method.Name)
        {
            case "Truncate" when argValues.Count == 1:
                mode = CalendarArithmetic.MidpointToZero;
                break;

            case "Floor" when argValues.Count == 1:
                mode = CalendarArithmetic.MidpointToNegativeInfinity;
                break;

            case "Ceiling" when argValues.Count == 1:
                mode = CalendarArithmetic.MidpointToPositiveInfinity;
                break;

            case "Round":
                if (argValues.Count == 1)
                {
                    decimals = 0;
                    mode = CalendarArithmetic.MidpointToEven;
                }
                else if (argValues.Count == 2)
                {
                    var secondParamType = method.Parameters[1].Type;
                    if (secondParamType.SpecialType == SpecialType.System_Int32)
                    {
                        if (!TryGetConstantInt(GetOrderedArgOp(inv, 1), out decimals))
                        {
                            throw new ContractLoweringException("decimal.Round decimals argument must be a compile-time constant integer (0..28).");
                        }
                    }
                    else if (secondParamType.OriginalDefinition.ToDisplayString() == "System.MidpointRounding")
                    {
                        if (!TryGetConstantInt(GetOrderedArgOp(inv, 1), out mode))
                        {
                            throw new ContractLoweringException("decimal.Round MidpointRounding mode must be a compile-time constant.");
                        }
                    }
                    else
                    {
                        return false;
                    }
                }
                else if (argValues.Count == 3)
                {
                    if (!TryGetConstantInt(GetOrderedArgOp(inv, 1), out decimals))
                    {
                        throw new ContractLoweringException("decimal.Round decimals argument must be a compile-time constant integer (0..28).");
                    }
                    if (!TryGetConstantInt(GetOrderedArgOp(inv, 2), out mode))
                    {
                        throw new ContractLoweringException("decimal.Round MidpointRounding mode must be a compile-time constant.");
                    }
                }
                else
                {
                    return false;
                }
                break;

            default:
                return false;
        }

        if (decimals < 0 || decimals > 28)
        {
            throw new ContractLoweringException($"decimal.Round decimals value {decimals} is out of valid range 0..28.");
        }

        if (mode is < CalendarArithmetic.MidpointToEven or > CalendarArithmetic.MidpointToPositiveInfinity)
        {
            throw new ContractLoweringException($"Unsupported MidpointRounding mode ({mode}).");
        }

        var decType = _compilation.GetSpecialType(SpecialType.System_Decimal);
        var x = _translator.ConvertTo(_translator.UnwrapRefinedIfApplicable(argValues[0]), decType);
        var scaleInt = BigInteger.Pow(10, decimals);
        var scaleReal = scaleInt.ToString(CultureInfo.InvariantCulture) + ".0";
        var scaledX = decimals == 0 ? x.Term : $"(* {x.Term} {scaleReal})";

        if (_inQuantifierDepth > 0)
        {
            // Pure inline expression inside a quantifier (no top-level declare-const)
            var inlineTerm = BuildInlineRoundingTerm(scaledX, x.Term, scaleReal, decimals, mode);
            result = new SmtTypedTerm(inlineTerm, decType);
            return true;
        }

        var key = $"{x.Term}|{decimals}|{mode}";
        if (!_memoizedRoundTerms.TryGetValue(key, out var roundedTerm))
        {
            var kName = $"rnd_k_{++_ssaCounter}";
            _decls.Add($"(declare-const {kName} Int)");
            var kReal = $"(to_real {kName})";

            switch (mode)
            {
                case CalendarArithmetic.MidpointToEven:
                    AddAssertion($"(<= (- {kReal} 0.5) {scaledX})", "DecimalRound", "Round lower bound", loc);
                    AddAssertion($"(<= {scaledX} (+ {kReal} 0.5))", "DecimalRound", "Round upper bound", loc);
                    AddAssertion(
                        $"(=> (or (= {scaledX} (- {kReal} 0.5)) (= {scaledX} (+ {kReal} 0.5))) (= (mod {kName} 2) 0))",
                        "DecimalRound", "Round ToEven tie-breaker", loc);
                    break;

                case CalendarArithmetic.MidpointAwayFromZero:
                    AddAssertion($"(<= (- {kReal} 0.5) {scaledX})", "DecimalRound", "Round lower bound", loc);
                    AddAssertion($"(<= {scaledX} (+ {kReal} 0.5))", "DecimalRound", "Round upper bound", loc);
                    AddAssertion($"(=> (= {scaledX} (+ {kReal} 0.5)) (< {x.Term} 0.0))", "DecimalRound", "Round AwayFromZero positive tie-breaker", loc);
                    AddAssertion($"(=> (= {scaledX} (- {kReal} 0.5)) (> {x.Term} 0.0))", "DecimalRound", "Round AwayFromZero negative tie-breaker", loc);
                    break;

                case CalendarArithmetic.MidpointToZero:
                    AddAssertion($"(=> (>= {x.Term} 0.0) (and (<= {kReal} {scaledX}) (< {scaledX} (to_real (+ {kName} 1)))))", "DecimalRound", "Truncate non-negative", loc);
                    AddAssertion($"(=> (< {x.Term} 0.0) (and (< (to_real (- {kName} 1)) {scaledX}) (<= {scaledX} {kReal})))", "DecimalRound", "Truncate negative", loc);
                    break;

                case CalendarArithmetic.MidpointToNegativeInfinity:
                    AddAssertion($"(<= {kReal} {scaledX})", "DecimalRound", "Floor lower bound", loc);
                    AddAssertion($"(< {scaledX} (to_real (+ {kName} 1)))", "DecimalRound", "Floor upper bound", loc);
                    break;

                case CalendarArithmetic.MidpointToPositiveInfinity:
                    AddAssertion($"(< (to_real (- {kName} 1)) {scaledX})", "DecimalRound", "Ceiling lower bound", loc);
                    AddAssertion($"(<= {scaledX} {kReal})", "DecimalRound", "Ceiling upper bound", loc);
                    break;
            }

            roundedTerm = decimals == 0 ? kReal : $"(/ {kReal} {scaleReal})";
            _memoizedRoundTerms[key] = roundedTerm;
        }

        result = new SmtTypedTerm(roundedTerm, decType);
        return true;
    }

    private static string BuildInlineRoundingTerm(string scaledX, string rawX, string scaleReal, int decimals, int mode)
    {
        var f = $"(to_int {scaledX})";
        var r = $"(- {scaledX} (to_real {f}))";
        var kExpr = mode switch
        {
            CalendarArithmetic.MidpointToEven =>
                $"(ite (< {r} 0.5) {f} (ite (> {r} 0.5) (+ {f} 1) (ite (= (mod {f} 2) 0) {f} (+ {f} 1))))",
            CalendarArithmetic.MidpointAwayFromZero =>
                $"(ite (< {r} 0.5) {f} (ite (> {r} 0.5) (+ {f} 1) (ite (>= {rawX} 0.0) (+ {f} 1) {f})))",
            CalendarArithmetic.MidpointToZero =>
                $"(ite (or (>= {rawX} 0.0) (= {r} 0.0)) {f} (+ {f} 1))",
            CalendarArithmetic.MidpointToNegativeInfinity => f,
            CalendarArithmetic.MidpointToPositiveInfinity =>
                $"(ite (= {r} 0.0) {f} (+ {f} 1))",
            _ => f
        };

        return decimals == 0 ? $"(to_real {kExpr})" : $"(/ (to_real {kExpr}) {scaleReal})";
    }

    private bool TryLowerCalendarStaticMember(ITypeSymbol containingType, string memberName, ITypeSymbol memberType, out SmtTypedTerm result)
    {
        result = default!;
        var kind = CalendarArithmetic.Classify(containingType);
        if (kind == CalendarTypeKind.None) return false;

        switch (kind)
        {
            case CalendarTypeKind.DateOnly:
                if (memberName == "MinValue") { result = new SmtTypedTerm("0", memberType); return true; }
                if (memberName == "MaxValue") { result = new SmtTypedTerm(CalendarArithmetic.DateOnlyMaxDayNumber.ToString(CultureInfo.InvariantCulture), memberType); return true; }
                break;

            case CalendarTypeKind.DateTime:
                if (memberName == "MinValue") { result = new SmtTypedTerm("0", memberType); return true; }
                if (memberName == "MaxValue") { result = new SmtTypedTerm(CalendarArithmetic.DateTimeMaxTicks.ToString(CultureInfo.InvariantCulture), memberType); return true; }
                if (memberName == "UnixEpoch") { result = new SmtTypedTerm(CalendarArithmetic.UnixEpochTicks.ToString(CultureInfo.InvariantCulture), memberType); return true; }
                break;

            case CalendarTypeKind.DateTimeOffset:
                if (memberName == "MinValue") { result = new SmtTypedTerm("(mk-VerDateTimeOffset 0 0)", memberType); return true; }
                if (memberName == "MaxValue") { result = new SmtTypedTerm($"(mk-VerDateTimeOffset {CalendarArithmetic.DateTimeMaxTicks} 0)", memberType); return true; }
                if (memberName == "UnixEpoch") { result = new SmtTypedTerm($"(mk-VerDateTimeOffset {CalendarArithmetic.UnixEpochTicks} 0)", memberType); return true; }
                break;

            case CalendarTypeKind.TimeSpan:
                if (memberName == "Zero") { result = new SmtTypedTerm("0", memberType); return true; }
                if (memberName == "MinValue") { result = new SmtTypedTerm(CalendarArithmetic.FormatLongInt(CalendarArithmetic.TimeSpanMinTicks), memberType); return true; }
                if (memberName == "MaxValue") { result = new SmtTypedTerm(CalendarArithmetic.FormatLongInt(CalendarArithmetic.TimeSpanMaxTicks), memberType); return true; }
                if (memberName == "TicksPerDay") { result = new SmtTypedTerm(_translator.FormatLongLiteral(CalendarArithmetic.TicksPerDay), memberType); return true; }
                if (memberName == "TicksPerHour") { result = new SmtTypedTerm(_translator.FormatLongLiteral(CalendarArithmetic.TicksPerHour), memberType); return true; }
                if (memberName == "TicksPerMinute") { result = new SmtTypedTerm(_translator.FormatLongLiteral(CalendarArithmetic.TicksPerMinute), memberType); return true; }
                if (memberName == "TicksPerSecond") { result = new SmtTypedTerm(_translator.FormatLongLiteral(CalendarArithmetic.TicksPerSecond), memberType); return true; }
                if (memberName == "TicksPerMillisecond") { result = new SmtTypedTerm(_translator.FormatLongLiteral(CalendarArithmetic.TicksPerMillisecond), memberType); return true; }
                break;
        }

        return false;
    }

    private bool TryLowerCalendarObjectCreation(
        IObjectCreationOperation objCreate,
        string gamma,
        BlockEnv env,
        bool emitObligations,
        int inlineDepth,
        out SmtTypedTerm result)
    {
        result = default!;
        var targetType = objCreate.Type;
        var kind = CalendarArithmetic.Classify(targetType);
        if (kind == CalendarTypeKind.None) return false;

        var loc = objCreate.Syntax.GetLocation();

        switch (kind)
        {
            case CalendarTypeKind.DateOnly:
                if (objCreate.Arguments.Length == 0)
                {
                    result = new SmtTypedTerm("0", targetType!);
                    return true;
                }
                if (objCreate.Arguments.Length == 3 &&
                    TryGetConstantInt(objCreate.Arguments[0].Value, out var y) &&
                    TryGetConstantInt(objCreate.Arguments[1].Value, out var m) &&
                    TryGetConstantInt(objCreate.Arguments[2].Value, out var d))
                {
                    if (!CalendarArithmetic.TryComputeDayNumber(y, m, d, out var dayNum))
                    {
                        throw new ContractLoweringException($"Invalid DateOnly({y}, {m}, {d}) literal.");
                    }
                    result = new SmtTypedTerm(dayNum.ToString(CultureInfo.InvariantCulture), targetType!);
                    return true;
                }
                throw new ContractLoweringException($"Only constant DateOnly(year, month, day) or DateOnly.FromDayNumber(...) is supported in '{objCreate.Syntax}'.");

            case CalendarTypeKind.DateTime:
                if (objCreate.Arguments.Length == 0)
                {
                    result = new SmtTypedTerm("0", targetType!);
                    return true;
                }
                if (objCreate.Arguments.Length == 1 && objCreate.Arguments[0].Parameter?.Type.SpecialType == SpecialType.System_Int64)
                {
                    var ticks = _translator.IntTermForIndex(LowerExpression(objCreate.Arguments[0].Value, gamma, env, emitObligations, inlineDepth));
                    EmitDateTimeRangeObligation(ticks, $"DateTime ticks out of range in '{objCreate.Syntax}'", loc, gamma);
                    result = new SmtTypedTerm(ticks, targetType!);
                    return true;
                }
                if (objCreate.Arguments.Length is 3 or 6 &&
                    TryGetConstantInt(objCreate.Arguments[0].Value, out var dy) &&
                    TryGetConstantInt(objCreate.Arguments[1].Value, out var dm) &&
                    TryGetConstantInt(objCreate.Arguments[2].Value, out var dd))
                {
                    int hh = 0, mm = 0, ss = 0;
                    if (objCreate.Arguments.Length == 6 &&
                        (!TryGetConstantInt(objCreate.Arguments[3].Value, out hh) ||
                         !TryGetConstantInt(objCreate.Arguments[4].Value, out mm) ||
                         !TryGetConstantInt(objCreate.Arguments[5].Value, out ss)))
                    {
                        throw new ContractLoweringException($"DateTime(y, m, d, h, min, s) requires constant arguments in '{objCreate.Syntax}'.");
                    }
                    if (!CalendarArithmetic.TryComputeDayNumber(dy, dm, dd, out var dtDay) ||
                        hh < 0 || hh > 23 || mm < 0 || mm > 59 || ss < 0 || ss > 59)
                    {
                        throw new ContractLoweringException($"Invalid DateTime literal '{objCreate.Syntax}'.");
                    }
                    var totalTicks = (long)dtDay * CalendarArithmetic.TicksPerDay +
                                     hh * CalendarArithmetic.TicksPerHour +
                                     mm * CalendarArithmetic.TicksPerMinute +
                                     ss * CalendarArithmetic.TicksPerSecond;
                    result = new SmtTypedTerm(totalTicks.ToString(CultureInfo.InvariantCulture), targetType!);
                    return true;
                }
                throw new ContractLoweringException($"Unsupported DateTime constructor '{objCreate.Syntax}'.");

            case CalendarTypeKind.TimeSpan:
                if (objCreate.Arguments.Length == 0)
                {
                    result = new SmtTypedTerm("0", targetType!);
                    return true;
                }
                if (objCreate.Arguments.Length == 1 && objCreate.Arguments[0].Parameter?.Type.SpecialType == SpecialType.System_Int64)
                {
                    var ticks = _translator.IntTermForIndex(LowerExpression(objCreate.Arguments[0].Value, gamma, env, emitObligations, inlineDepth));
                    EmitTimeSpanRangeObligation(ticks, $"TimeSpan ticks out of range in '{objCreate.Syntax}'", loc, gamma);
                    result = new SmtTypedTerm(ticks, targetType!);
                    return true;
                }
                if (objCreate.Arguments.Length == 3)
                {
                    var tsH = _translator.IntTermForIndex(LowerExpression(objCreate.Arguments[0].Value, gamma, env, emitObligations, inlineDepth));
                    var tsM = _translator.IntTermForIndex(LowerExpression(objCreate.Arguments[1].Value, gamma, env, emitObligations, inlineDepth));
                    var tsS = _translator.IntTermForIndex(LowerExpression(objCreate.Arguments[2].Value, gamma, env, emitObligations, inlineDepth));
                    var sum = $"(+ (* {tsH} {CalendarArithmetic.TicksPerHour}) (* {tsM} {CalendarArithmetic.TicksPerMinute}) (* {tsS} {CalendarArithmetic.TicksPerSecond}))";
                    EmitTimeSpanRangeObligation(sum, $"TimeSpan overflow in '{objCreate.Syntax}'", loc, gamma);
                    result = new SmtTypedTerm(sum, targetType!);
                    return true;
                }
                if (objCreate.Arguments.Length == 4)
                {
                    var tsD = _translator.IntTermForIndex(LowerExpression(objCreate.Arguments[0].Value, gamma, env, emitObligations, inlineDepth));
                    var tsH = _translator.IntTermForIndex(LowerExpression(objCreate.Arguments[1].Value, gamma, env, emitObligations, inlineDepth));
                    var tsM = _translator.IntTermForIndex(LowerExpression(objCreate.Arguments[2].Value, gamma, env, emitObligations, inlineDepth));
                    var tsS = _translator.IntTermForIndex(LowerExpression(objCreate.Arguments[3].Value, gamma, env, emitObligations, inlineDepth));
                    var sum = $"(+ (* {tsD} {CalendarArithmetic.TicksPerDay}) (* {tsH} {CalendarArithmetic.TicksPerHour}) (* {tsM} {CalendarArithmetic.TicksPerMinute}) (* {tsS} {CalendarArithmetic.TicksPerSecond}))";
                    EmitTimeSpanRangeObligation(sum, $"TimeSpan overflow in '{objCreate.Syntax}'", loc, gamma);
                    result = new SmtTypedTerm(sum, targetType!);
                    return true;
                }
                throw new ContractLoweringException($"Unsupported TimeSpan constructor '{objCreate.Syntax}'.");

            case CalendarTypeKind.DateTimeOffset:
                if (objCreate.Arguments.Length == 0)
                {
                    result = new SmtTypedTerm("(mk-VerDateTimeOffset 0 0)", targetType!);
                    return true;
                }
                throw new ContractLoweringException($"Unsupported DateTimeOffset constructor '{objCreate.Syntax}'.");
        }

        return false;
    }

    private bool TryLowerCalendarInvocation(
        IInvocationOperation inv,
        SmtTypedTerm? recvTerm,
        IReadOnlyList<SmtTypedTerm> argValues,
        string gamma,
        BlockEnv env,
        bool emitObligations,
        int inlineDepth,
        out SmtTypedTerm result)
    {
        result = default!;
        var method = inv.TargetMethod;
        var kind = CalendarArithmetic.Classify(method.ContainingType);
        if (kind == CalendarTypeKind.None) return false;

        var loc = inv.Syntax.GetLocation();
        var intType = _compilation.GetSpecialType(SpecialType.System_Int32);
        var longType = _compilation.GetSpecialType(SpecialType.System_Int64);
        var boolType = _compilation.GetSpecialType(SpecialType.System_Boolean);

        if (method.Name == "Equals" && recvTerm is not null && argValues.Count == 1)
        {
            if (kind == CalendarTypeKind.DateTimeOffset)
            {
                result = new SmtTypedTerm($"(= (dto_utc_ticks {recvTerm.Term}) (dto_utc_ticks {argValues[0].Term}))", boolType);
            }
            else
            {
                result = new SmtTypedTerm($"(= {recvTerm.Term} {argValues[0].Term})", boolType);
            }
            return true;
        }

        if (method.Name == "CompareTo" && recvTerm is not null && argValues.Count == 1)
        {
            var l = kind == CalendarTypeKind.DateTimeOffset ? $"(dto_utc_ticks {recvTerm.Term})" : recvTerm.Term;
            var r = kind == CalendarTypeKind.DateTimeOffset ? $"(dto_utc_ticks {argValues[0].Term})" : argValues[0].Term;
            var cmp = $"(ite (< {l} {r}) (- 1) (ite (> {l} {r}) 1 0))";
            result = _translator.ConvertTo(new SmtTypedTerm(cmp, intType), intType);
            return true;
        }

        switch (kind)
        {
            case CalendarTypeKind.DateOnly:
                if (method.Name == "FromDayNumber" && argValues.Count == 1)
                {
                    var dn = _translator.IntTermForIndex(argValues[0]);
                    EmitDateOnlyRangeObligation(dn, $"DateOnly.FromDayNumber out of range in '{inv.Syntax}'", loc, gamma);
                    result = new SmtTypedTerm(dn, method.ReturnType);
                    return true;
                }
                if (method.Name == "AddDays" && recvTerm is not null && argValues.Count == 1)
                {
                    var delta = _translator.IntTermForIndex(argValues[0]);
                    var next = $"(+ {recvTerm.Term} {delta})";
                    EmitDateOnlyRangeObligation(next, $"DateOnly.AddDays out of valid calendar range in '{inv.Syntax}'", loc, gamma);
                    result = new SmtTypedTerm(next, method.ReturnType);
                    return true;
                }
                break;

            case CalendarTypeKind.DateTime:
                if (recvTerm is not null && argValues.Count == 1)
                {
                    string? deltaTicks = method.Name switch
                    {
                        "AddDays" => LowerExactTickDelta(GetOrderedArgOp(inv, 0), CalendarArithmetic.TicksPerDay, gamma, env, emitObligations, inlineDepth),
                        "AddHours" => LowerExactTickDelta(GetOrderedArgOp(inv, 0), CalendarArithmetic.TicksPerHour, gamma, env, emitObligations, inlineDepth),
                        "AddMinutes" => LowerExactTickDelta(GetOrderedArgOp(inv, 0), CalendarArithmetic.TicksPerMinute, gamma, env, emitObligations, inlineDepth),
                        "AddSeconds" => LowerExactTickDelta(GetOrderedArgOp(inv, 0), CalendarArithmetic.TicksPerSecond, gamma, env, emitObligations, inlineDepth),
                        "AddMilliseconds" => LowerExactTickDelta(GetOrderedArgOp(inv, 0), CalendarArithmetic.TicksPerMillisecond, gamma, env, emitObligations, inlineDepth),
                        "AddTicks" => _translator.IntTermForIndex(argValues[0]),
                        "Add" when CalendarArithmetic.Classify(argValues[0].Type) == CalendarTypeKind.TimeSpan => argValues[0].Term,
                        "Subtract" when CalendarArithmetic.Classify(argValues[0].Type) == CalendarTypeKind.TimeSpan => $"(- {argValues[0].Term})",
                        _ => null
                    };

                    if (deltaTicks is not null)
                    {
                        var next = $"(+ {recvTerm.Term} {deltaTicks})";
                        EmitDateTimeRangeObligation(next, $"DateTime.{method.Name} out of valid calendar range in '{inv.Syntax}'", loc, gamma);
                        result = new SmtTypedTerm(next, method.ReturnType);
                        return true;
                    }

                    if (method.Name == "Subtract" && CalendarArithmetic.Classify(argValues[0].Type) == CalendarTypeKind.DateTime)
                    {
                        result = new SmtTypedTerm($"(- {recvTerm.Term} {argValues[0].Term})", method.ReturnType);
                        return true;
                    }
                }
                break;

            case CalendarTypeKind.DateTimeOffset:
                if (recvTerm is not null)
                {
                    if (method.Name == "ToUnixTimeSeconds" && argValues.Count == 0)
                    {
                        var secs = $"(- (div (dto_utc_ticks {recvTerm.Term}) {CalendarArithmetic.TicksPerSecond}) {CalendarArithmetic.UnixEpochSeconds})";
                        result = _translator.ConvertTo(new SmtTypedTerm(secs, longType), longType);
                        return true;
                    }
                    if (method.Name == "ToUnixTimeMilliseconds" && argValues.Count == 0)
                    {
                        var ms = $"(- (div (dto_utc_ticks {recvTerm.Term}) {CalendarArithmetic.TicksPerMillisecond}) {CalendarArithmetic.UnixEpochMilliseconds})";
                        result = _translator.ConvertTo(new SmtTypedTerm(ms, longType), longType);
                        return true;
                    }
                    if (argValues.Count == 1)
                    {
                        string? deltaTicks = method.Name switch
                        {
                            "AddDays" => LowerExactTickDelta(GetOrderedArgOp(inv, 0), CalendarArithmetic.TicksPerDay, gamma, env, emitObligations, inlineDepth),
                            "AddHours" => LowerExactTickDelta(GetOrderedArgOp(inv, 0), CalendarArithmetic.TicksPerHour, gamma, env, emitObligations, inlineDepth),
                            "AddMinutes" => LowerExactTickDelta(GetOrderedArgOp(inv, 0), CalendarArithmetic.TicksPerMinute, gamma, env, emitObligations, inlineDepth),
                            "AddSeconds" => LowerExactTickDelta(GetOrderedArgOp(inv, 0), CalendarArithmetic.TicksPerSecond, gamma, env, emitObligations, inlineDepth),
                            "AddMilliseconds" => LowerExactTickDelta(GetOrderedArgOp(inv, 0), CalendarArithmetic.TicksPerMillisecond, gamma, env, emitObligations, inlineDepth),
                            "AddTicks" => _translator.IntTermForIndex(argValues[0]),
                            "Add" when CalendarArithmetic.Classify(argValues[0].Type) == CalendarTypeKind.TimeSpan => argValues[0].Term,
                            "Subtract" when CalendarArithmetic.Classify(argValues[0].Type) == CalendarTypeKind.TimeSpan => $"(- {argValues[0].Term})",
                            _ => null
                        };

                        if (deltaTicks is not null)
                        {
                            var newUtc = $"(+ (dto_utc_ticks {recvTerm.Term}) {deltaTicks})";
                            var off = $"(dto_offset_ticks {recvTerm.Term})";
                            EmitDateTimeRangeObligation(newUtc, $"DateTimeOffset.{method.Name} UTC ticks out of range in '{inv.Syntax}'", loc, gamma);
                            EmitDateTimeRangeObligation($"(+ {newUtc} {off})", $"DateTimeOffset.{method.Name} local ticks out of range in '{inv.Syntax}'", loc, gamma);
                            result = new SmtTypedTerm($"(mk-VerDateTimeOffset {newUtc} {off})", method.ReturnType);
                            return true;
                        }

                        if (method.Name == "Subtract" && CalendarArithmetic.Classify(argValues[0].Type) == CalendarTypeKind.DateTimeOffset)
                        {
                            result = new SmtTypedTerm($"(- (dto_utc_ticks {recvTerm.Term}) (dto_utc_ticks {argValues[0].Term}))", method.ReturnType);
                            return true;
                        }
                    }
                }
                break;

            case CalendarTypeKind.TimeSpan:
                if (method.IsStatic && argValues.Count == 1)
                {
                    string? ticksTerm = method.Name switch
                    {
                        "FromTicks" => _translator.IntTermForIndex(argValues[0]),
                        "FromDays" => LowerExactTickDelta(GetOrderedArgOp(inv, 0), CalendarArithmetic.TicksPerDay, gamma, env, emitObligations, inlineDepth),
                        "FromHours" => LowerExactTickDelta(GetOrderedArgOp(inv, 0), CalendarArithmetic.TicksPerHour, gamma, env, emitObligations, inlineDepth),
                        "FromMinutes" => LowerExactTickDelta(GetOrderedArgOp(inv, 0), CalendarArithmetic.TicksPerMinute, gamma, env, emitObligations, inlineDepth),
                        "FromSeconds" => LowerExactTickDelta(GetOrderedArgOp(inv, 0), CalendarArithmetic.TicksPerSecond, gamma, env, emitObligations, inlineDepth),
                        "FromMilliseconds" => LowerExactTickDelta(GetOrderedArgOp(inv, 0), CalendarArithmetic.TicksPerMillisecond, gamma, env, emitObligations, inlineDepth),
                        _ => null
                    };

                    if (ticksTerm is not null)
                    {
                        EmitTimeSpanRangeObligation(ticksTerm, $"TimeSpan.{method.Name} overflow in '{inv.Syntax}'", loc, gamma);
                        result = new SmtTypedTerm(ticksTerm, method.ReturnType);
                        return true;
                    }
                }

                if (recvTerm is not null)
                {
                    if (method.Name == "Add" && argValues.Count == 1)
                    {
                        var sum = $"(+ {recvTerm.Term} {argValues[0].Term})";
                        EmitTimeSpanRangeObligation(sum, $"TimeSpan.Add overflow in '{inv.Syntax}'", loc, gamma);
                        result = new SmtTypedTerm(sum, method.ReturnType);
                        return true;
                    }
                    if (method.Name == "Subtract" && argValues.Count == 1)
                    {
                        var diff = $"(- {recvTerm.Term} {argValues[0].Term})";
                        EmitTimeSpanRangeObligation(diff, $"TimeSpan.Subtract overflow in '{inv.Syntax}'", loc, gamma);
                        result = new SmtTypedTerm(diff, method.ReturnType);
                        return true;
                    }
                    if (method.Name == "Negate" && argValues.Count == 0)
                    {
                        EmitSafetyObligation("Overflow", $"TimeSpan.Negate overflow on TimeSpan.MinValue in '{inv.Syntax}'", loc, gamma,
                            $"(not (= {recvTerm.Term} {CalendarArithmetic.FormatLongInt(CalendarArithmetic.TimeSpanMinTicks)}))");
                        result = new SmtTypedTerm($"(- {recvTerm.Term})", method.ReturnType);
                        return true;
                    }
                    if (method.Name == "Duration" && argValues.Count == 0)
                    {
                        EmitSafetyObligation("Overflow", $"TimeSpan.Duration overflow on TimeSpan.MinValue in '{inv.Syntax}'", loc, gamma,
                            $"(not (= {recvTerm.Term} {CalendarArithmetic.FormatLongInt(CalendarArithmetic.TimeSpanMinTicks)}))");
                        result = new SmtTypedTerm($"(ite (< {recvTerm.Term} 0) (- {recvTerm.Term}) {recvTerm.Term})", method.ReturnType);
                        return true;
                    }
                }
                break;
        }

        return false;
    }

    private bool TryLowerCalendarBinaryOperator(
        BinaryOperatorKind kind,
        SmtTypedTerm left,
        SmtTypedTerm right,
        ITypeSymbol resultType,
        string gamma,
        Location loc,
        out SmtTypedTerm result)
    {
        result = default!;
        var lk = CalendarArithmetic.Classify(left.Type);
        var rk = CalendarArithmetic.Classify(right.Type);
        if (lk == CalendarTypeKind.None && rk == CalendarTypeKind.None)
        {
            return false;
        }

        var boolType = _compilation.GetSpecialType(SpecialType.System_Boolean);

        // Comparisons between same calendar types
        if (lk == rk && kind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals or
            BinaryOperatorKind.LessThan or BinaryOperatorKind.LessThanOrEqual or
            BinaryOperatorKind.GreaterThan or BinaryOperatorKind.GreaterThanOrEqual)
        {
            var l = lk == CalendarTypeKind.DateTimeOffset ? $"(dto_utc_ticks {left.Term})" : left.Term;
            var r = rk == CalendarTypeKind.DateTimeOffset ? $"(dto_utc_ticks {right.Term})" : right.Term;
            var expr = kind switch
            {
                BinaryOperatorKind.Equals => $"(= {l} {r})",
                BinaryOperatorKind.NotEquals => $"(not (= {l} {r}))",
                BinaryOperatorKind.LessThan => $"(< {l} {r})",
                BinaryOperatorKind.LessThanOrEqual => $"(<= {l} {r})",
                BinaryOperatorKind.GreaterThan => $"(> {l} {r})",
                BinaryOperatorKind.GreaterThanOrEqual => $"(>= {l} {r})",
                _ => throw new InvalidOperationException()
            };
            result = new SmtTypedTerm(expr, boolType);
            return true;
        }

        // DateTime +/- TimeSpan, DateTime - DateTime
        if (lk == CalendarTypeKind.DateTime && rk == CalendarTypeKind.TimeSpan &&
            kind is BinaryOperatorKind.Add or BinaryOperatorKind.Subtract)
        {
            var op = kind == BinaryOperatorKind.Add ? "+" : "-";
            var next = $"({op} {left.Term} {right.Term})";
            EmitDateTimeRangeObligation(next, $"DateTime arithmetic out of range ({kind})", loc, gamma);
            result = new SmtTypedTerm(next, resultType);
            return true;
        }

        if (lk == CalendarTypeKind.DateTime && rk == CalendarTypeKind.DateTime && kind == BinaryOperatorKind.Subtract)
        {
            result = new SmtTypedTerm($"(- {left.Term} {right.Term})", resultType);
            return true;
        }

        // DateTimeOffset +/- TimeSpan, DateTimeOffset - DateTimeOffset
        if (lk == CalendarTypeKind.DateTimeOffset && rk == CalendarTypeKind.TimeSpan &&
            kind is BinaryOperatorKind.Add or BinaryOperatorKind.Subtract)
        {
            var op = kind == BinaryOperatorKind.Add ? "+" : "-";
            var newUtc = $"({op} (dto_utc_ticks {left.Term}) {right.Term})";
            var off = $"(dto_offset_ticks {left.Term})";
            EmitDateTimeRangeObligation(newUtc, $"DateTimeOffset UTC arithmetic out of range ({kind})", loc, gamma);
            EmitDateTimeRangeObligation($"(+ {newUtc} {off})", $"DateTimeOffset local arithmetic out of range ({kind})", loc, gamma);
            result = new SmtTypedTerm($"(mk-VerDateTimeOffset {newUtc} {off})", resultType);
            return true;
        }

        if (lk == CalendarTypeKind.DateTimeOffset && rk == CalendarTypeKind.DateTimeOffset && kind == BinaryOperatorKind.Subtract)
        {
            result = new SmtTypedTerm($"(- (dto_utc_ticks {left.Term}) (dto_utc_ticks {right.Term}))", resultType);
            return true;
        }

        // TimeSpan +/- TimeSpan
        if (lk == CalendarTypeKind.TimeSpan && rk == CalendarTypeKind.TimeSpan &&
            kind is BinaryOperatorKind.Add or BinaryOperatorKind.Subtract)
        {
            var op = kind == BinaryOperatorKind.Add ? "+" : "-";
            var next = $"({op} {left.Term} {right.Term})";
            EmitTimeSpanRangeObligation(next, $"TimeSpan arithmetic overflow ({kind})", loc, gamma);
            result = new SmtTypedTerm(next, resultType);
            return true;
        }

        return false;
    }

    private string LowerExactTickDelta(
        IOperation argOp,
        long ticksPerUnit,
        string gamma,
        BlockEnv env,
        bool emitObligations,
        int inlineDepth)
    {
        // Unwrap implicit int/long -> double conversion so integral day/hour/second counts stay exact integer arithmetic!
        var inner = argOp;
        while (inner is IConversionOperation conv &&
               conv.Type?.SpecialType == SpecialType.System_Double &&
               IsExactIntegralType(conv.Operand.Type))
        {
            inner = conv.Operand;
        }

        if (IsExactIntegralType(inner.Type))
        {
            var intVal = _translator.IntTermForIndex(LowerExpression(inner, gamma, env, emitObligations, inlineDepth));
            return $"(* {intVal} {ticksPerUnit})";
        }

        // Allow exact finite double constants whose scaled ticks are exact integers (e.g. AddHours(1.5))
        if (argOp.ConstantValue.HasValue && argOp.ConstantValue.Value is double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d))
            {
                throw new ContractLoweringException("Non-finite floating-point calendar offset is not supported.");
            }
            var exactTicks = Math.Round(d * ticksPerUnit);
            if (Math.Abs(d * ticksPerUnit - exactTicks) > 1e-6 ||
                exactTicks < long.MinValue || exactTicks > long.MaxValue)
            {
                throw new ContractLoweringException($"Floating-point calendar offset '{d}' cannot be represented as an exact tick count.");
            }
            return CalendarArithmetic.FormatLongInt((long)exactTicks);
        }

        throw new ContractLoweringException(
            $"Calendar offset '{argOp.Syntax}' must be an integral value or exact finite constant (symbolic double offsets can be NaN/Infinity).");
    }

    private static bool IsExactIntegralType(ITypeSymbol? type) =>
        type?.SpecialType is
            SpecialType.System_SByte or SpecialType.System_Byte or
            SpecialType.System_Int16 or SpecialType.System_UInt16 or
            SpecialType.System_Int32 or SpecialType.System_UInt32 or
            SpecialType.System_Int64 or SpecialType.System_UInt64;

    private void EmitDateOnlyRangeObligation(string dayNumTerm, string desc, Location loc, string gamma)
    {
        EmitSafetyObligation(
            "Overflow",
            desc,
            loc,
            gamma,
            $"(and (>= {dayNumTerm} {CalendarArithmetic.DateOnlyMinDayNumber}) (<= {dayNumTerm} {CalendarArithmetic.DateOnlyMaxDayNumber}))");
    }

    private void EmitDateTimeRangeObligation(string ticksTerm, string desc, Location loc, string gamma)
    {
        EmitSafetyObligation(
            "Overflow",
            desc,
            loc,
            gamma,
            $"(and (>= {ticksTerm} {CalendarArithmetic.DateTimeMinTicks}) (<= {ticksTerm} {CalendarArithmetic.DateTimeMaxTicks}))");
    }

    private void EmitTimeSpanRangeObligation(string ticksTerm, string desc, Location loc, string gamma)
    {
        EmitSafetyObligation(
            "Overflow",
            desc,
            loc,
            gamma,
            $"(and (>= {ticksTerm} {CalendarArithmetic.FormatLongInt(CalendarArithmetic.TimeSpanMinTicks)}) (<= {ticksTerm} {CalendarArithmetic.FormatLongInt(CalendarArithmetic.TimeSpanMaxTicks)}))");
    }
}
