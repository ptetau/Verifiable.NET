using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Verifiable.Analyzers.Smt;

internal static class CalendarArithmetic
{
    internal static string? CalendarType(ITypeSymbol? type)
    {
        if (type?.Name is not ("DateOnly" or "TimeSpan" or "DateTime")) return null;
        return type.ContainingNamespace.ToDisplayString() == "System" &&
               type.ContainingAssembly.GetTypeByMetadataName("System.Object")?.SpecialType == SpecialType.System_Object
            ? type.Name : null;
    }

    internal static bool IsRound(IMethodSymbol method) =>
        method.ContainingType.SpecialType == SpecialType.System_Decimal && method.Name == "Round" &&
        method.Parameters.Length == 2 && method.Parameters[1].Type.SpecialType == SpecialType.System_Int32;

    internal static bool Supports(IMethodSymbol method)
    {
        if (IsRound(method)) return true;
        if (method.Parameters.Length != 1) return false;
        var p = method.Parameters[0].Type;
        return (CalendarType(method.ContainingType), method.Name, p.SpecialType) switch
        {
            ("DateOnly", "AddDays" or "FromDayNumber", SpecialType.System_Int32) => true,
            ("DateTime", "AddDays" or "AddHours", SpecialType.System_Double) => true,
            ("DateTime", "AddTicks", SpecialType.System_Int64) => true,
            ("TimeSpan", "FromTicks", SpecialType.System_Int64) => true,
            ("TimeSpan", "FromHours" or "FromDays", SpecialType.System_Int32) => true,
            ("TimeSpan", "FromSeconds", SpecialType.System_Int64) => true,
            ("TimeSpan", "Add" or "Subtract", _) => CalendarType(p) == "TimeSpan",
            ("DateTime", "Add", _) => CalendarType(p) == "TimeSpan",
            ("DateTime", "Subtract", _) => CalendarType(p) is "DateTime" or "TimeSpan",
            _ => false
        };
    }

    internal static string Number(BigInteger n) => n.Sign < 0
        ? $"(- {BigInteger.Abs(n).ToString(CultureInfo.InvariantCulture)})" : n.ToString(CultureInfo.InvariantCulture);

    internal static (string Min, string Max) Bounds(string type) => type switch
    {
        "DateOnly" => ("0", "3652058"),
        "DateTime" => ("0", "3155378975999999999"),
        "TimeSpan" => ("(- 9223372036854775808)", "9223372036854775807"),
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    internal static string InRange(string type, string term)
    {
        var (min, max) = Bounds(type);
        return $"(and (<= {min} {term}) (<= {term} {max}))";
    }

    internal static string Round(string amount, string scale, int? constant)
    {
        string Power(int digits) => "1" + new string('0', digits);
        var power = constant is { } k ? Power(k) : Enumerable.Range(0, 28).Reverse()
            .Aggregate(Power(28), (rest, i) => $"(ite (= {scale} {i}) {Power(i)} {rest})");
        // to_int is floor, including for negatives. At a midpoint choose the even neighbour.
        return $"(let ((p {power})) (let ((x (* {amount} p))) (let ((n (to_int x))) " +
               "(let ((f (- x n))) (/ (ite (or (< f 0.5) (and (= f 0.5) (= (mod n 2) 0))) n (+ n 1)) p)))))";
    }

    // TimeSpan.TotalSeconds rounds twice: long -> double, then division by 10^7.
    // Its monotonicity allows exact literal comparisons without approximating doubles as Reals.
    internal static string CompareSeconds(string ticks, double seconds, BinaryOperatorKind op)
    {
        BigInteger First(bool strict)
        {
            BigInteger lo = long.MinValue, hi = (BigInteger)long.MaxValue + 1;
            while (lo < hi)
            {
                var mid = lo + ((hi - lo) >> 1);
                var s = (double)(long)mid / TimeSpan.TicksPerSecond;
                if (strict ? s > seconds : s >= seconds) hi = mid;
                else lo = mid + 1;
            }
            return lo;
        }
        if (double.IsNaN(seconds)) return op == BinaryOperatorKind.NotEquals ? "true" : "false";
        var at = Number(First(false));
        var after = Number(First(true));
        return op switch
        {
            BinaryOperatorKind.LessThan => $"(< {ticks} {at})",
            BinaryOperatorKind.LessThanOrEqual => $"(< {ticks} {after})",
            BinaryOperatorKind.GreaterThan => $"(>= {ticks} {after})",
            BinaryOperatorKind.GreaterThanOrEqual => $"(>= {ticks} {at})",
            BinaryOperatorKind.Equals => $"(and (>= {ticks} {at}) (< {ticks} {after}))",
            BinaryOperatorKind.NotEquals => $"(or (< {ticks} {at}) (>= {ticks} {after}))",
            _ => throw new ContractLoweringException("TotalSeconds supports only comparisons with numeric constants.")
        };
    }
}
