using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Verifiable.Analyzers.Smt;
using Xunit;

namespace Verifiable.Analyzers.Tests;

public class CalendarArithmeticTests
{
    [Fact]
    public async Task D19_DecimalRound_ProvesScaleInvariantsModesAndBothPostconditionForms()
    {
        const string source = """
            using System;
            using Verifiable.Contracts;

            namespace Sample;

            [Verifiable]
            public static class BillingRules
            {
                public static bool HasTwoDecimalPlaces(decimal amount) => decimal.Round(amount, 2) == amount;

                public static bool SumPreservesTwoDecimalPlaces(decimal a, decimal b)
                {
                    Spec.Requires(HasTwoDecimalPlaces(a) && HasTwoDecimalPlaces(b) && a >= -1000000m && a <= 1000000m && b >= -1000000m && b <= 1000000m);
                    Spec.Ensures<bool>(r => r);
                    return HasTwoDecimalPlaces(a + b);
                }

                public static decimal RoundBothFormsLambda(decimal x)
                {
                    Spec.Ensures<decimal>(r => r == decimal.Round(x, 2));
                    return decimal.Round(x, 2);
                }

                public static decimal RoundBothFormsSpecResult(decimal x)
                {
                    Spec.Ensures(Spec.Result<decimal>() == decimal.Round(x, 2));
                    return decimal.Round(x, 2);
                }

                public static bool CheckRoundingModes()
                {
                    Spec.Ensures<bool>(r => r);
                    return decimal.Round(2.5m, 0) == 2m
                        && decimal.Round(3.5m, 0) == 4m
                        && decimal.Round(-2.5m, 0) == -2m
                        && decimal.Round(-3.5m, 0) == -4m
                        && decimal.Round(2.5m, 0, MidpointRounding.AwayFromZero) == 3m
                        && decimal.Round(-2.5m, 0, MidpointRounding.AwayFromZero) == -3m
                        && HasTwoDecimalPlaces(1.23m)
                        && !HasTwoDecimalPlaces(1.234m);
                }
            }
            """;

        await CheckClean(source);
    }

    [Fact]
    public async Task D19_PureDateOnlyDateTimeDateTimeOffsetTimeSpan_VerifyAndCheckRanges()
    {
        const string source = """
            using System;
            using Verifiable.Contracts;

            namespace Sample;

            [Verifiable]
            public static class ScheduleRules
            {
                public static bool IsPastDue(DateOnly dueDate, DateOnly asOf, int graceDays)
                {
                    Spec.Requires(graceDays >= 0 && dueDate.DayNumber + graceDays <= DateOnly.MaxValue.DayNumber);
                    Spec.Ensures<bool>(r => r == (asOf > dueDate.AddDays(graceDays)));
                    return asOf > dueDate.AddDays(graceDays);
                }

                public static bool DayOfWeekAdvancesBySeven(DateOnly date)
                {
                    Spec.Requires(date.DayNumber + 7 <= DateOnly.MaxValue.DayNumber);
                    Spec.Ensures<bool>(r => r);
                    return date.AddDays(7).DayOfWeek == date.DayOfWeek;
                }

                public static bool DateTimeDifferenceTotalSeconds(DateTime start)
                {
                    Spec.Requires(start.Ticks <= DateTime.MaxValue.Ticks - TimeSpan.TicksPerHour);
                    Spec.Ensures<bool>(r => r);
                    var end = start.AddHours(1);
                    return (end - start).TotalSeconds == 3600.0;
                }

                public static bool DateTimeOffsetDifference(DateTimeOffset start)
                {
                    Spec.Requires(start.UtcTicks <= DateTime.MaxValue.Ticks - TimeSpan.TicksPerDay &&
                                  start.Ticks <= DateTime.MaxValue.Ticks - TimeSpan.TicksPerDay);
                    Spec.Ensures<bool>(r => r);
                    var next = start.AddDays(1);
                    return (next - start).TotalHours == 24.0;
                }
            }
            """;

        await CheckClean(source);
    }

    [Fact]
    public async Task UnboundedDateOnlyAddDays_AndTimeSpanMinValueNegate_AreCaughtAsVER102()
    {
        const string unboundedDate = """
            using System;
            using Verifiable.Contracts;

            namespace Sample;

            [Verifiable]
            public static class UnboundedCalendar
            {
                public static bool UnsafePastDue(DateOnly dueDate, DateOnly asOf, int graceDays)
                {
                    Spec.Requires(graceDays >= 0);
                    Spec.Ensures<bool>(r => r == (asOf > dueDate.AddDays(graceDays)));
                    return asOf > dueDate.AddDays(graceDays);
                }
            }
            """;

        await CheckExpecting(unboundedDate, "VER102");

        const string minTimeSpan = """
            using System;
            using Verifiable.Contracts;

            namespace Sample;

            [Verifiable]
            public static class TimeSpanOverflow
            {
                public static TimeSpan UnsafeNegate(TimeSpan ts) => -ts;
            }
            """;

        await CheckExpecting(minTimeSpan, "VER102");
    }

    [Fact]
    public async Task AmbientClockReads_StillFailGatekeeperWithVER004()
    {
        const string source = """
            using System;
            using Verifiable.Contracts;

            namespace Sample;

            [Verifiable]
            public static class ClockCalls
            {
                public static DateTime BadNow() => DateTime.Now;
                public static DateTime BadToday() => DateTime.Today;
                public static DateTimeOffset BadOffsetNow() => DateTimeOffset.UtcNow;
            }
            """;

        var diags = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableGatekeeperAnalyzer());
        Assert.Equal(3, diags.Count(d => d.Id == "VER004"));
    }

    private static async Task CheckClean(string source)
    {
        var diags = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableGatekeeperAnalyzer(), new VerifiableSmtAnalyzer());
        Assert.Empty(diags.Where(d => d.Id == "AD0001" || d.Id == "VER104" || d.Severity == DiagnosticSeverity.Error));
    }

    private static async Task CheckExpecting(string source, string expectedId)
    {
        var diags = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableGatekeeperAnalyzer(), new VerifiableSmtAnalyzer());
        Assert.DoesNotContain(diags, d => d.Id == "AD0001" || d.Id == "VER104");
        Assert.Contains(diags, d => d.Id == expectedId);
    }
}
