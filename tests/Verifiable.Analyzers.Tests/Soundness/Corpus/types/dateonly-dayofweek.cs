// A correct program: seven days later is the same weekday, and 1 Jan 2024 was a Monday.
// @expect: proved
using System;
using Verifiable.Contracts;

namespace Corpus.Types;

[Verifiable]
public static class Weekdays
{
    public static bool SameWeekdayNextWeek(int dayNumber)
    {
        Spec.Requires(dayNumber >= 0 && dayNumber <= 3000000, "Well inside the DateOnly range.");
        Spec.Ensures<bool>(r => r == true, "Adding seven days keeps the weekday.");
        var d = DateOnly.FromDayNumber(dayNumber);
        return d.AddDays(7).DayOfWeek == d.DayOfWeek;
    }

    public static bool NewYear2024IsMonday()
    {
        Spec.Ensures<bool>(r => r == true, "1 January 2024 was a Monday.");
        return new DateOnly(2024, 1, 1).DayOfWeek == DayOfWeek.Monday;
    }
}
