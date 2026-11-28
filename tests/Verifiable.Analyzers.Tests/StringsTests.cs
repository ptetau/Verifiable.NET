using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Verifiable.Analyzers.Smt;
using Xunit;

namespace Verifiable.Analyzers.Tests;

public class StringsTests
{
    [Fact]
    public async Task D14_AddressHasAnyText_IsNullOrWhiteSpaceProvesCleanly()
    {
        const string source = """
            using Verifiable.Contracts;

            namespace Sample;

            [Verifiable]
            public readonly record struct Address(string? Street, string? City, string? PostalCode)
            {
                public bool HasAnyText()
                {
                    Spec.Ensures(Spec.Result<bool>() == (
                        !string.IsNullOrWhiteSpace(Street) ||
                        !string.IsNullOrWhiteSpace(City) ||
                        !string.IsNullOrWhiteSpace(PostalCode)));
                    return !string.IsNullOrWhiteSpace(Street)
                        || !string.IsNullOrWhiteSpace(City)
                        || !string.IsNullOrWhiteSpace(PostalCode);
                }
            }
            """;

        await CheckClean(source);
    }

    [Fact]
    public async Task Trim_Contains_StartsWith_EndsWith_And_BoundedJoin_VerifyProperties()
    {
        const string source = """
            using System;
            using Verifiable.Contracts;

            namespace Sample;

            [Verifiable]
            public static class TextOps
            {
                public static string Normalize(string? input)
                {
                    Spec.Ensures<string>(r => r.Length <= (input == null ? 0 : input.Length));
                    if (input == null) return "";
                    return input.Trim();
                }

                public static bool TrimMatchesIsNullOrWhiteSpace(string? input)
                {
                    Spec.Ensures<bool>(r => r);
                    if (input == null) return string.IsNullOrWhiteSpace(input);
                    return string.IsNullOrWhiteSpace(input) == (input.Trim().Length == 0);
                }

                public static bool KnownLiteralChecks()
                {
                    Spec.Ensures<bool>(r => r);
                    return "  hello  ".Trim() == "hello"
                        && "abcdef".Contains("bcd")
                        && "abcdef".StartsWith("abc", StringComparison.Ordinal)
                        && !"abcdef".StartsWith("def", StringComparison.Ordinal)
                        && "abcdef".EndsWith("def", StringComparison.Ordinal)
                        && !"abcdef".EndsWith("abc", StringComparison.Ordinal)
                        && "abcdef".Contains('c')
                        && "abcdef".StartsWith('a')
                        && "abcdef".EndsWith('f');
                }

                public static string FormatPair(string? first, string? second)
                {
                    Spec.Requires(first == "A" && second == "B");
                    Spec.Ensures<string>(r => r == "A, B");
                    return string.Join(", ", first, second);
                }

                public static string JoinWithNullAndChar(string? maybeNull)
                {
                    Spec.Requires(maybeNull == null);
                    Spec.Ensures<string>(r => r == "-end");
                    return string.Join('-', maybeNull, "end");
                }
            }
            """;

        await CheckClean(source);
    }

    [Fact]
    public async Task UnguardedNullableStringInstanceCall_EmitsVER102()
    {
        const string source = """
            using System;
            using Verifiable.Contracts;

            namespace Sample;

            [Verifiable]
            public static class NullChecks
            {
                public static bool BadStartsWith(string? value)
                {
                    Spec.Ensures<bool>(r => r == value!.StartsWith("a", StringComparison.Ordinal));
                    return value!.StartsWith("a", StringComparison.Ordinal);
                }
            }
            """;

        await CheckExpecting(source, "VER102");
    }

    [Fact]
    public async Task NonOrdinalStringComparison_IsRejectedByGatekeeper()
    {
        const string source = """
            using System;
            using Verifiable.Contracts;

            namespace Sample;

            [Verifiable]
            public static class CultureChecks
            {
                public static bool BadCulture(string s)
                {
                    Spec.Requires(s != null);
                    return s.StartsWith("a", StringComparison.CurrentCulture);
                }
            }
            """;

        var diags = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableGatekeeperAnalyzer());
        Assert.Contains(diags, d => d.Id == "VER006");
    }

    [Fact]
    public async Task UnboundedStringJoin_IsRejectedWithVER101()
    {
        const string source = """
            using System.Collections.Immutable;
            using Verifiable.Contracts;

            namespace Sample;

            [Verifiable]
            public static class JoinChecks
            {
                public static string JoinAll(ImmutableList<string> parts)
                {
                    Spec.Ensures<string>(r => r != null);
                    return string.Join(",", parts);
                }
            }
            """;

        await CheckExpecting(source, "VER101");
    }

    private static string Wrap(string body) => $$"""
        using System;
        using Verifiable.Contracts;

        namespace Sample;

        [Verifiable]
        public static class T
        {
        {{body}}
        }
        """;

    [Theory]
    [InlineData("StartsWith")]
    [InlineData("EndsWith")]
    public async Task CultureSensitiveStartsWithEndsWith_IsRejectedWithVER101(string method)
    {
        var source = Wrap($$"""
            public static bool M(string s)
            {
                Spec.Requires(s != null);
                Spec.Ensures<bool>(r => !r || s.Length >= 1);
                return s.{{method}}("a");
            }
            """);

        var diags = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableGatekeeperAnalyzer(), new VerifiableSmtAnalyzer());
        var ver101 = Assert.Single(diags, d => d.Id == "VER101");
        Assert.Contains("StringComparison.Ordinal", ver101.GetMessage());
    }

    [Fact]
    public async Task OrdinalStartsWithEndsWith_StayProved()
    {
        var source = Wrap("""
            public static bool M(string s)
            {
                Spec.Requires(s != null);
                Spec.Ensures<bool>(r => !r || s.Length >= 1);
                return s.StartsWith("a", StringComparison.Ordinal) || s.EndsWith("b", StringComparison.Ordinal);
            }
            """);

        await CheckClean(source);
    }

    [Fact]
    public async Task LengthOfNullableString_NeedsNonNullProof()
    {
        var source = Wrap("""
            public static int Len(string? s)
            {
                Spec.Ensures<int>(r => r >= 0);
                return s.Length;
            }
            """);

        var diags = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableGatekeeperAnalyzer(), new VerifiableSmtAnalyzer());
        Assert.Contains(diags, d => d.Severity == DiagnosticSeverity.Error && d.Id.StartsWith("VER1", System.StringComparison.Ordinal));
    }

    [Fact]
    public async Task LengthOfGuardedString_Proves()
    {
        var source = Wrap("""
            public static int Len(string? s)
            {
                Spec.Ensures<int>(r => r >= 0);
                if (s == null) return 0;
                return s.Length;
            }
            """);

        await CheckClean(source);
    }

    [Fact]
    public async Task NullableValueWithoutHasValue_IsRejected()
    {
        var source = Wrap("""
            public static int Unwrap(int? x)
            {
                Spec.Ensures<int>(r => r == x.Value);
                return x.Value;
            }
            """);

        var diags = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableGatekeeperAnalyzer(), new VerifiableSmtAnalyzer());
        Assert.Contains(diags, d => d.Severity == DiagnosticSeverity.Error && d.Id.StartsWith("VER1", System.StringComparison.Ordinal));
    }

    [Fact]
    public async Task NullableValueAndCastAfterNullCheck_Prove()
    {
        var source = Wrap("""
            public static int Value(int? x)
            {
                Spec.Ensures<int>(r => r == (x == null ? 0 : x.Value));
                if (x == null) return 0;
                return x.Value;
            }

            public static int Cast(int? x)
            {
                Spec.Ensures<int>(r => r == (x == null ? 0 : x.Value));
                if (x == null) return 0;
                return (int)x;
            }

            public static long WideningCast(int? x)
            {
                Spec.Requires(x != null);
                Spec.Ensures<long>(r => r == x.Value);
                return (long)x;
            }
            """);

        await CheckClean(source);
    }

    [Fact]
    public async Task ExplicitCastOfEmptyNullable_IsRejected()
    {
        var source = Wrap("""
            public static int Cast(int? x)
            {
                Spec.Ensures<int>(r => r >= int.MinValue);
                return (int)x;
            }
            """);

        var diags = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableGatekeeperAnalyzer(), new VerifiableSmtAnalyzer());
        Assert.Contains(diags, d => d.Severity == DiagnosticSeverity.Error && d.Id.StartsWith("VER1", System.StringComparison.Ordinal));
    }

    [Fact]
    public async Task EqualsThroughInterfaceOnNullableString_IsRejected()
    {
        var source = Wrap("""
            public static bool Same(string? a, string? b)
            {
                Spec.Ensures<bool>(r => r == (a == b));
                IEquatable<string?> e = a!;
                return e.Equals(b);
            }
            """);

        var diags = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableGatekeeperAnalyzer(), new VerifiableSmtAnalyzer());
        Assert.Contains(diags, d => d.Severity == DiagnosticSeverity.Error && d.Id.StartsWith("VER1", System.StringComparison.Ordinal));
    }

    [Fact]
    public async Task EqualsOnGuardedString_Proves()
    {
        var source = Wrap("""
            public static bool Same(string a, string? b)
            {
                Spec.Requires(a != null);
                Spec.Ensures<bool>(r => r == (a == b));
                IEquatable<string?> e = a;
                return e.Equals(b);
            }
            """);

        await CheckClean(source);
    }

    [Fact]
    public async Task ListPatternOnString_IsRejectedWithVER101()
    {
        var source = Wrap("""
            public static bool StartsWithA(string s)
            {
                Spec.Requires(s != null);
                Spec.Ensures<bool>(r => r);
                return s is ['a', ..];
            }
            """);

        await CheckExpecting(source, "VER101");
    }

    [Fact]
    public async Task NegatedTypePatternOnEmptyNullable_IsRejected()
    {
        var source = Wrap("""
            public static bool IsEmpty(int? x)
            {
                Spec.Ensures<bool>(r => !r);
                return x is not int;
            }
            """);

        var diags = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableGatekeeperAnalyzer(), new VerifiableSmtAnalyzer());
        Assert.Contains(diags, d => d.Severity == DiagnosticSeverity.Error && d.Id.StartsWith("VER1", System.StringComparison.Ordinal));
    }

    [Fact]
    public async Task TypePatternsOnNullablesAndStrings_Prove()
    {
        var source = Wrap("""
            public static bool HasValue(int? x)
            {
                Spec.Ensures<bool>(r => r == (x != null));
                return x is int;
            }

            public static bool IsEmpty(int? x)
            {
                Spec.Ensures<bool>(r => r == (x == null));
                return x is not int;
            }

            public static bool NonNull(string? s)
            {
                Spec.Ensures<bool>(r => r == (s != null));
                return s is string;
            }
            """);

        await CheckClean(source);
    }

    [Fact]
    public async Task VarPatternMatchesNull_IsRejected()
    {
        var source = Wrap("""
            public static bool Matches(string? s)
            {
                Spec.Ensures<bool>(r => !r || s != null);
                return s is var t;
            }
            """);

        var diags = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableGatekeeperAnalyzer(), new VerifiableSmtAnalyzer());
        Assert.Contains(diags, d => d.Severity == DiagnosticSeverity.Error && d.Id.StartsWith("VER1", System.StringComparison.Ordinal));
    }

    [Fact]
    public async Task VarPatternAlwaysMatches_Proves()
    {
        var source = Wrap("""
            public static bool Matches(string? s)
            {
                Spec.Ensures<bool>(r => r);
                return s is var t;
            }
            """);

        await CheckClean(source);
    }

    [Fact]
    public async Task RelationalPatternOnNullableInt_Proves()
    {
        var source = Wrap("""
            public static bool Big(int? x)
            {
                Spec.Ensures<bool>(r => r == (x != null && x.Value > 5));
                return x is > 5;
            }

            public static bool Range(int? x)
            {
                Spec.Ensures<bool>(r => !r || x != null);
                return x is >= 1 and <= 9;
            }
            """);

        await CheckClean(source);
    }

    [Fact]
    public async Task CheckedStringLengthPlusOne_Proves()
    {
        var source = Wrap("""
            [CheckedArithmetic]
            public static int LengthPlusOne(string s)
            {
                Spec.Requires(s != null);
                Spec.Ensures<int>(r => r == s.Length + 1 && r > 0);
                return s.Length + 1;
            }
            """);

        await CheckClean(source);
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
