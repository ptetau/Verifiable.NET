using Microsoft.CodeAnalysis;
using Verifiable.Analyzers.Smt;
using Xunit;

namespace Verifiable.Analyzers.Tests;

public class NullableCfgTests
{
    [Theory]
    [InlineData("a >= b", "a.HasValue && b.HasValue && a.Value >= b.Value")]
    [InlineData("a < 10m", "a.HasValue && a.Value < 10m")]
    [InlineData("a != b", "a.HasValue != b.HasValue || (a.HasValue && a.Value != b.Value)")]
    public async Task LiftedComparisonsMatchCSharp(string comparison, string expanded)
    {
        await Check($$"""
            public static bool Compare(decimal? a, decimal? b)
            {
                Spec.Ensures<bool>(r => r == ({{expanded}}));
                return {{comparison}};
            }
            """);
    }

    [Fact]
    public async Task LiftedComparisonInSpecDoesNotAssumePresence()
    {
        await Check("""
            public static bool Compare(decimal? a)
            {
                Spec.Requires(!a.HasValue);
                Spec.Ensures<bool>(r => r == (a >= 10m));
                return true;
            }
            """, "VER102");
    }

    [Fact]
    public async Task LiftedPromotionPreservesNull()
    {
        await Check("""
            public static bool Compare(int? a, decimal? b)
            {
                Spec.Ensures<bool>(r => r == (a.HasValue && b.HasValue && a.Value <= b.Value));
                return a <= b;
            }
            """);
    }

    [Fact]
    public async Task UnsupportedLiftedComparisonIsNotTreatedAsNumeric()
    {
        await Check("""
            public readonly record struct Threshold(int Value)
            {
                public static bool operator <(Threshold a, Threshold b) => false;
                public static bool operator >(Threshold a, Threshold b) => true;
            }
            public static bool Compare(Threshold? a, Threshold? b)
            {
                Spec.Ensures<bool>(r => true);
                return a < b;
            }
            """, "VER101");
    }

    [Fact]
    public async Task CoalesceInBodyAndSpecification()
    {
        await Check("""
            public static decimal OrZero(decimal? a)
            {
                Spec.Ensures<decimal>(r => r == (a.HasValue ? a.Value : 0m));
                return a ?? 0m;
            }
            public static string Text(string? text, string? alternate)
            {
                Spec.Ensures<string>(r => r == (text ?? alternate ?? ""));
                return text != null ? text : alternate != null ? alternate : "";
            }
            public static decimal? OrOther(decimal? a, decimal? b)
            {
                Spec.Ensures<decimal?>(r => r == (a ?? b));
                return a.HasValue ? a : b;
            }
            """);
    }

    [Fact]
    public async Task CoalesceEvaluatesFallbackOnlyWhenAbsent()
    {
        await Check("""
            public static int F(int? value, int divisor)
            {
                Spec.Requires(value.HasValue || divisor != 0);
                Spec.Ensures<int>(r => r == (value ?? (10 / divisor)));
                return value ?? (10 / divisor);
            }
            """);
    }

    [Fact]
    public async Task CoalesceWithUnsupportedReferenceSortFailsClosed()
    {
        await Check("""
            public static object First(object? a, object b)
            {
                Spec.Ensures<object>(r => true);
                return a ?? b;
            }
            """, "VER101");
    }

    [Fact]
    public async Task CoalesceDoesNotIgnoreNullCase()
    {
        await Check("""
            public static int OrZero(int? value)
            {
                Spec.Ensures<int>(r => r == (value ?? 1));
                return value ?? 0;
            }
            """, "VER102");
    }

    [Theory]
    [InlineData("Left != 0 || Right != 0", null)]
    [InlineData("Left != 0 && Right != 0", "VER102")]
    public async Task BooleanPostconditionOnStructUsesActualReturnValue(string body, string? expected)
    {
        await Check($$"""
            public bool Any()
            {
                Spec.Ensures(Spec.Result<bool>() == (Left != 0 || Right != 0));
                return {{body}};
            }
            """, expected);
    }

    [Fact]
    public async Task ResultInExecutableCodeIsNotASymbolicReturn()
    {
        await Check("""
            public static bool Bad()
            {
                Spec.Ensures<bool>(r => true);
                return Spec.Result<bool>();
            }
            """, "VER101");
    }

    private static async Task Check(string methods, string? expected = null)
    {
        var diagnostics = await AnalyzerTestHarness.AnalyzeAsync($$"""
            #nullable enable
            using Verifiable.Contracts;
            [Verifiable]
            public readonly record struct Pair(int Left, int Right)
            {
                {{methods}}
            }
            """, new VerifiableSmtAnalyzer());
        var failures = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error || d.Id is "AD0001" or "VER104");
        if (expected is null)
            Assert.Empty(failures);
        else
            Assert.Equal(expected, Assert.Single(failures).Id);
    }
}
