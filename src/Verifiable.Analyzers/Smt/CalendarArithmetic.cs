using System;
using System.Globalization;
using System.Numerics;
using Microsoft.CodeAnalysis;

namespace Verifiable.Analyzers.Smt;

internal enum CalendarTypeKind
{
    None = 0,
    DateOnly,
    DateTime,
    DateTimeOffset,
    TimeSpan
}

internal static class CalendarArithmetic
{
    public const int DateOnlyMinDayNumber = 0;          // 0001-01-01
    public const int DateOnlyMaxDayNumber = 3652058;    // 9999-12-31

    public const long TicksPerMillisecond = 10_000L;
    public const long TicksPerSecond = 10_000_000L;
    public const long TicksPerMinute = 600_000_000L;
    public const long TicksPerHour = 36_000_000_000L;
    public const long TicksPerDay = 864_000_000_000L;

    public const long DateTimeMinTicks = 0L;
    public const long DateTimeMaxTicks = 3_155_378_975_999_999_999L; // 9999-12-31 23:59:59.9999999
    public const long UnixEpochTicks = 621_355_968_000_000_000L;     // 1970-01-01 00:00:00 UTC
    public const long UnixEpochSeconds = 62_135_596_800L;
    public const long UnixEpochMilliseconds = 62_135_596_800_000L;

    public const long MaxOffsetTicks = 14L * TicksPerHour; // +/- 14:00
    public const long TimeSpanMinTicks = long.MinValue;
    public const long TimeSpanMaxTicks = long.MaxValue;

    // System.MidpointRounding values
    public const int MidpointToEven = 0;
    public const int MidpointAwayFromZero = 1;
    public const int MidpointToZero = 2;
    public const int MidpointToNegativeInfinity = 3;
    public const int MidpointToPositiveInfinity = 4;

    private static readonly int[] DaysToMonth365 = [0, 31, 59, 90, 120, 151, 181, 212, 243, 273, 304, 334, 365];
    private static readonly int[] DaysToMonth366 = [0, 31, 60, 91, 121, 152, 182, 213, 244, 274, 305, 335, 366];

    public static CalendarTypeKind Classify(ITypeSymbol? type)
    {
        if (type is null) return CalendarTypeKind.None;
        return type.OriginalDefinition.ToDisplayString() switch
        {
            "System.DateOnly" => CalendarTypeKind.DateOnly,
            "System.DateTime" => CalendarTypeKind.DateTime,
            "System.DateTimeOffset" => CalendarTypeKind.DateTimeOffset,
            "System.TimeSpan" => CalendarTypeKind.TimeSpan,
            _ => CalendarTypeKind.None
        };
    }

    public static bool IsTrustedCalendarMethod(IMethodSymbol method)
    {
        var kind = Classify(method.ContainingType);
        if (kind == CalendarTypeKind.None)
        {
            return false;
        }

        if (VerifiableSymbolHelpers.IsNondeterministicMember(method, out _))
        {
            return false;
        }

        var name = method.Name;
        return kind switch
        {
            CalendarTypeKind.DateOnly => name is
                "AddDays" or "FromDayNumber" or "CompareTo" or "Equals",
            CalendarTypeKind.DateTime => name is
                "AddDays" or "AddHours" or "AddMinutes" or "AddSeconds" or "AddMilliseconds" or
                "AddTicks" or "Add" or "Subtract" or "CompareTo" or "Equals",
            CalendarTypeKind.DateTimeOffset => name is
                "AddDays" or "AddHours" or "AddMinutes" or "AddSeconds" or "AddMilliseconds" or
                "AddTicks" or "Add" or "Subtract" or "ToUnixTimeSeconds" or "ToUnixTimeMilliseconds" or
                "CompareTo" or "Equals",
            CalendarTypeKind.TimeSpan => name is
                "FromTicks" or "FromDays" or "FromHours" or "FromMinutes" or "FromSeconds" or "FromMilliseconds" or
                "Add" or "Subtract" or "Negate" or "Duration" or "CompareTo" or "Equals",
            _ => false
        };
    }

    public static bool TryComputeDayNumber(int year, int month, int day, out int dayNumber)
    {
        dayNumber = 0;
        if (year < 1 || year > 9999 || month < 1 || month > 12)
        {
            return false;
        }

        var isLeap = (year % 4 == 0 && year % 100 != 0) || (year % 400 == 0);
        var table = isLeap ? DaysToMonth366 : DaysToMonth365;
        var daysInMonth = table[month] - table[month - 1];
        if (day < 1 || day > daysInMonth)
        {
            return false;
        }

        var y = year - 1;
        dayNumber = y * 365 + y / 4 - y / 100 + y / 400 + table[month - 1] + (day - 1);
        return true;
    }

    public static string FormatBigInt(BigInteger value)
    {
        if (value < 0)
        {
            return $"(- {BigInteger.Abs(value).ToString(CultureInfo.InvariantCulture)})";
        }
        return value.ToString(CultureInfo.InvariantCulture);
    }

    public static string FormatLongInt(long value)
    {
        if (value == long.MinValue) return "(- 9223372036854775808)";
        return value < 0
            ? $"(- {(-value).ToString(CultureInfo.InvariantCulture)})"
            : value.ToString(CultureInfo.InvariantCulture);
    }
}
