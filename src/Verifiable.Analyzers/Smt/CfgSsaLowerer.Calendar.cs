using System;
using System.Linq;
using System.Numerics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Verifiable.Analyzers.Smt;

internal sealed partial class CfgSsaLowerer
{
    private bool _usesDecimalRound;
    private IOperation? _inexactDecimalProducer;
    private bool _reportedDecimalPrecisionError;

    // Round is exact on a CLR decimal input, not on every intermediate Real in our
    // arithmetic model. Check the entire proof context, including inlined definitions
    // and premises, so an SSA binding or a precondition cannot hide a lossy operation.
    private void TrackDecimalPrecisionBoundary(IOperation op)
    {
        if (_reportedDecimalPrecisionError) return;
        if (op is IInvocationOperation call && CalendarArithmetic.IsRound(call.TargetMethod))
            _usesDecimalRound = true;
        else if (_inexactDecimalProducer == null && op.Type?.SpecialType == SpecialType.System_Decimal && op switch
        {
            ILiteralOperation or IDefaultValueOperation or IFieldReferenceOperation or IPropertyReferenceOperation
                or IParameterReferenceOperation or ILocalReferenceOperation or IFlowCaptureReferenceOperation
                or IParenthesizedOperation or ISimpleAssignmentOperation or ICoalesceOperation or IConditionalOperation => false,
            IUnaryOperation { OperatorKind: UnaryOperatorKind.Plus or UnaryOperatorKind.Minus } u =>
                u.Operand.Type?.SpecialType != SpecialType.System_Decimal,
            IConversionOperation c => c.Operand.Type?.SpecialType != SpecialType.System_Decimal,
            IInvocationOperation result when VerifiableSymbolHelpers.IsSpecCall(result.TargetMethod, "Result") => false,
            _ => true
        })
            _inexactDecimalProducer = op;

        if (_usesDecimalRound && _inexactDecimalProducer is { } producer)
        {
            _reportedDecimalPrecisionError = true;
            _errors.Add((_method.Name, producer.Syntax.GetLocation(),
                $"Cannot combine Decimal.Round with '{producer.Syntax}': exact Real arithmetic does not model CLR decimal intermediate precision. Round a decimal input or stored field instead."));
        }
    }

    private SmtTypedTerm CheckedCalendar(string term, ITypeSymbol type, string gamma, Location loc)
    {
        var calendar = CalendarArithmetic.CalendarType(type)!;
        AddObligation("Precondition", $"{calendar} result within its representable range", loc, gamma,
            CalendarArithmetic.InRange(calendar, term));
        return new SmtTypedTerm(term, type);
    }

    private static SmtTypedTerm CalendarConstant(string calendar, string name, ITypeSymbol type)
    {
        var (min, max) = CalendarArithmetic.Bounds(calendar);
        return new SmtTypedTerm(name switch
        {
            "MinValue" => min,
            "MaxValue" => max,
            "Zero" when calendar == "TimeSpan" => "0",
            _ => throw new ContractLoweringException($"Unsupported {calendar} constant '{name}'.")
        }, type);
    }

    private SmtTypedTerm CalendarMember(string calendar, string t, string name, ITypeSymbol type)
    {
        var days = calendar == "DateTime" ? $"(div {t} 864000000000)" : t;
        var value = (calendar, name) switch
        {
            ("DateOnly", "DayNumber") or ("TimeSpan" or "DateTime", "Ticks") => t,
            ("DateOnly" or "DateTime", "DayOfWeek") => $"(mod (+ {days} 1) 7)",
            ("DateTime", "Date") => $"(* {days} 864000000000)",
            ("DateTime", "TimeOfDay") => $"(mod {t} 864000000000)",
            _ => throw new ContractLoweringException($"Unsupported {calendar}.{name} access (TotalSeconds supports comparisons to constants).")
        };
        if (_types.UseBitVec && type.SpecialType is SpecialType.System_Int32 or SpecialType.System_Int64)
            value = $"((_ int2bv {(type.SpecialType == SpecialType.System_Int32 ? 32 : 64)}) {value})";
        return new SmtTypedTerm(value, type);
    }

    private SmtTypedTerm LowerCalendarBinary(BinaryOperatorKind op, SmtTypedTerm left, SmtTypedTerm right,
        ITypeSymbol resultType, string gamma, Location loc)
    {
        var l = CalendarArithmetic.CalendarType(left.Type);
        var r = CalendarArithmetic.CalendarType(right.Type);
        var compare = op switch
        {
            BinaryOperatorKind.Equals => "=", BinaryOperatorKind.NotEquals => "distinct",
            BinaryOperatorKind.LessThan => "<", BinaryOperatorKind.LessThanOrEqual => "<=",
            BinaryOperatorKind.GreaterThan => ">", BinaryOperatorKind.GreaterThanOrEqual => ">=",
            _ => null
        };
        if (compare != null && l == r)
            return new SmtTypedTerm($"({compare} {left.Term} {right.Term})", resultType);
        if (l is "TimeSpan" or "DateTime" && r == "TimeSpan" &&
            op is BinaryOperatorKind.Add or BinaryOperatorKind.Subtract ||
            l == "DateTime" && r == "DateTime" && op == BinaryOperatorKind.Subtract)
            return CheckedCalendar($"({(op == BinaryOperatorKind.Add ? "+" : "-")} {left.Term} {right.Term})", resultType, gamma, loc);
        throw new ContractLoweringException($"Unsupported calendar arithmetic '{left.Type} {op} {right.Type}'.");
    }

    private SmtTypedTerm LowerCalendarInvocation(IInvocationOperation inv, string gamma, BlockEnv env, bool emit, int depth)
    {
        var method = inv.TargetMethod;
        var loc = inv.Syntax.GetLocation();
        if (!CalendarArithmetic.Supports(method))
            throw new ContractLoweringException($"Unsupported overload '{method}'.");
        SmtTypedTerm Lower(IOperation operation) => LowerExpression(operation, gamma, env, emit, depth);
        string Int(IOperation operation) => _translator.IntTermForIndex(Lower(operation));

        if (CalendarArithmetic.IsRound(method))
        {
            var args = inv.Arguments.Select(a => (a.Parameter!.Ordinal, Term: Lower(a.Value), a.Value.ConstantValue))
                .OrderBy(a => a.Ordinal).ToArray();
            var scale = _translator.IntTermForIndex(args[1].Term);
            var k = args[1].ConstantValue is { HasValue: true, Value: int n } ? n : (int?)null;
            if (k is < 0 or > 28)
                throw new ContractLoweringException("Decimal.Round scale must be between 0 and 28.");
            if (k is null)
                AddObligation("Precondition", "Decimal.Round scale between 0 and 28", loc, gamma,
                    $"(and (<= 0 {scale}) (<= {scale} 28))");
            return new SmtTypedTerm(CalendarArithmetic.Round(args[0].Term.Term, scale, k), method.ReturnType);
        }

        var receiver = inv.Instance is null ? null : Lower(inv.Instance).Term;
        var arg = inv.Arguments[0].Value;
        string delta;
        if (arg.Type?.SpecialType == SpecialType.System_Double)
        {
            var unit = method.Name == "AddHours" ? TimeSpan.TicksPerHour : TimeSpan.TicksPerDay;
            if (arg is IConversionOperation { Operand.Type.SpecialType: SpecialType.System_Int32, OperatorMethod: null } widen)
                delta = $"(* {Int(widen.Operand)} {unit})";
            else if (arg.ConstantValue is { HasValue: true, Value: double d } && Math.Abs(d) <= 3652059 * 24.0)
            {
                // Use the integral + fractional decomposition of AddUnits. Only accept exact
                // milliseconds so runtimes using millisecond rounding agree as well.
                var whole = (long)d;
                var frac = (d - whole) * unit;
                var ticks = (BigInteger)whole * unit + (long)frac;
                var legacyMilliseconds = Math.Round(d * (unit / TimeSpan.TicksPerMillisecond), MidpointRounding.AwayFromZero);
                if (frac != (long)frac || ticks != (BigInteger)legacyMilliseconds * TimeSpan.TicksPerMillisecond)
                    throw new ContractLoweringException($"Unsupported fractional calendar offset '{arg.Syntax}'.");
                delta = CalendarArithmetic.Number(ticks);
            }
            else throw new ContractLoweringException("AddDays/AddHours requires an int offset or an exact constant offset.");
        }
        else
        {
            delta = Int(arg);
            delta = method.Name switch
            {
                "FromDays" => $"(* {delta} 864000000000)",
                "FromHours" => $"(* {delta} 36000000000)",
                "FromSeconds" => $"(* {delta} 10000000)",
                _ => delta
            };
        }
        var result = receiver is null ? delta : $"({(method.Name == "Subtract" ? "-" : "+")} {receiver} {delta})";
        return CheckedCalendar(result, method.ReturnType, gamma, loc);
    }

    private SmtTypedTerm LowerCalendarConstructor(string calendar, IObjectCreationOperation obj, string gamma,
        BlockEnv env, bool emit, int depth)
    {
        if (obj.Arguments.Length == 0 && obj.Initializer is null) return new SmtTypedTerm("0", obj.Type!);
        if (obj.Initializer is null && calendar is "DateTime" or "TimeSpan" && obj.Arguments.Length == 1 &&
            obj.Arguments[0].Parameter?.Type.SpecialType == SpecialType.System_Int64)
        {
            var t = LowerExpression(obj.Arguments[0].Value, gamma, env, emit, depth);
            return CheckedCalendar(_translator.IntTermForIndex(t), obj.Type!, gamma, obj.Syntax.GetLocation());
        }
        if (obj.Initializer is null && calendar is "DateOnly" or "DateTime" && obj.Arguments.Length == 3 &&
            obj.Arguments.All(a => a.Value.ConstantValue is { HasValue: true, Value: int }))
        {
            var parts = obj.Arguments.OrderBy(a => a.Parameter!.Ordinal).Select(a => (int)a.Value.ConstantValue.Value!).ToArray();
            try
            {
                var ticks = new DateTime(parts[0], parts[1], parts[2]).Ticks;
                return new SmtTypedTerm(CalendarArithmetic.Number(calendar == "DateOnly" ? ticks / TimeSpan.TicksPerDay : ticks), obj.Type!);
            }
            catch (ArgumentOutOfRangeException)
            {
                throw new ContractLoweringException($"Invalid calendar date '{obj.Syntax}'.");
            }
        }
        throw new ContractLoweringException($"Unsupported calendar constructor '{obj.Syntax}'.");
    }

    private SmtTypedTerm? TryCompareTotalSeconds(IBinaryOperation bin, string gamma, BlockEnv env, bool emit, int depth)
    {
        static IOperation? Receiver(IOperation op) =>
            op is IPropertyReferenceOperation { Property.Name: "TotalSeconds", Instance: { } recv } &&
            CalendarArithmetic.CalendarType(recv.Type) == "TimeSpan" ? recv : null;
        var receiver = Receiver(bin.LeftOperand);
        var constant = bin.RightOperand.ConstantValue;
        var op = bin.OperatorKind;
        if (receiver is null)
        {
            receiver = Receiver(bin.RightOperand);
            if (receiver is null) return null;
            constant = bin.LeftOperand.ConstantValue;
            op = op switch
            {
                BinaryOperatorKind.GreaterThan => BinaryOperatorKind.LessThan,
                BinaryOperatorKind.GreaterThanOrEqual => BinaryOperatorKind.LessThanOrEqual,
                BinaryOperatorKind.LessThan => BinaryOperatorKind.GreaterThan,
                BinaryOperatorKind.LessThanOrEqual => BinaryOperatorKind.GreaterThanOrEqual,
                _ => op
            };
        }
        if (constant is not { HasValue: true, Value: double d } || bin.Type?.SpecialType != SpecialType.System_Boolean)
            throw new ContractLoweringException("TotalSeconds supports only comparisons with numeric constants.");
        var ticks = LowerExpression(receiver, gamma, env, emit, depth);
        return new SmtTypedTerm(CalendarArithmetic.CompareSeconds(ticks.Term, d, op), bin.Type);
    }
}
