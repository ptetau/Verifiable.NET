using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Verifiable.Analyzers.Smt;
using Xunit;

namespace Verifiable.Analyzers.Tests;

public class CalendarArithmeticTests
{
    private static async Task<string[]> Errors(string methods)
    {
        var diagnostics = await AnalyzerTestHarness.AnalyzeAsync(
            "using System; using Verifiable.Contracts; [Verifiable] public static class Rules { " + methods + " }",
            new VerifiableSmtAnalyzer(), new VerifiableGatekeeperAnalyzer());
        Assert.DoesNotContain(diagnostics, d => d.Id == "AD0001" || d.Id.StartsWith("CS") && d.Severity == DiagnosticSeverity.Error);
        return diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()).ToArray();
    }

    [Fact]
    public async Task DecimalMidpointsAndNeighbors()
    {
        Assert.Empty(await Errors("""
            public static bool F() {
                Spec.Ensures<bool>(r => r);
                return decimal.Round(1.225m, 2) == 1.22m && decimal.Round(-1.225m, 2) == -1.22m &&
                    decimal.Round(1.235m, 2) == 1.24m && decimal.Round(-1.235m, 2) == -1.24m &&
                    decimal.Round(-1.23501m, 2) == -1.24m && decimal.Round(-1.23499m, 2) == -1.23m;
            }
            public static decimal Dynamic(decimal x, int places) {
                Spec.Requires(places >= 0 && places <= 28);
                Spec.Ensures<decimal>(r => r == decimal.Round(x, places));
                return decimal.Round(decimals: places, d: x);
            }
            """));
    }

    [Fact]
    public async Task CalendarValuesAreExactAndBounded()
    {
        Assert.Empty(await Errors("""
            public static bool Due(DateOnly due, DateOnly asOf, int graceDays) {
                Spec.Requires(graceDays >= 0 && graceDays <= DateOnly.MaxValue.DayNumber - due.DayNumber);
                Spec.Ensures<bool>(r => r == (asOf > due.AddDays(graceDays)));
                return asOf.DayNumber > due.DayNumber + graceDays;
            }
            public static bool DateArithmetic() {
                Spec.Ensures<bool>(r => r);
                var d = new DateTime(2024, 2, 28);
                var tomorrow = d.AddDays(1);
                var elapsed = tomorrow.AddHours(1.5) - d;
                return tomorrow.DayOfWeek == DayOfWeek.Thursday && elapsed > TimeSpan.Zero
                    && 91800.0 == elapsed.TotalSeconds && (d + elapsed).Ticks == tomorrow.AddHours(1.5).Ticks;
            }
            public static bool RoundedSeconds() {
                Spec.Ensures<bool>(r => r);
                return TimeSpan.MaxValue.TotalSeconds == 922337203685.47754;
            }
            """));
    }

    [Theory]
    [InlineData("public static decimal F(decimal x, int n) => decimal.Round(x, n);", "VER102")]
    [InlineData("public static bool F() { Spec.Ensures<bool>(r => r); return decimal.Round(1.25m, 1) == 1.3m; }", "VER102")]
    [InlineData("public static DateOnly F(DateOnly day) => day.AddDays(1);", "VER102")]
    [InlineData("public static TimeSpan F() => TimeSpan.MaxValue + new TimeSpan(1);", "VER102")]
    [InlineData("public static DateTime F(DateTime d, double x) => d.AddHours(x);", "VER101")]
    [InlineData("public static decimal F(decimal x) => decimal.Round(x, MidpointRounding.AwayFromZero);", "VER101")]
    [InlineData("public static int F(DateOnly day) => day.AddMonths(1).DayNumber;", "VER101")]
    [InlineData("public static DateTime F() => DateTime.Now.AddHours(0);", "VER004")]
    [InlineData("public static DateTime F() => DateTime.UtcNow.AddDays(0);", "VER004")]
    [InlineData("public static DateTime F() => DateTime.Today;", "VER004")]
    public async Task RefutesOrRejectsUnsafeOperations(string method, string diagnostic) =>
        Assert.Contains(await Errors(method), error => error.Contains(diagnostic));
}
