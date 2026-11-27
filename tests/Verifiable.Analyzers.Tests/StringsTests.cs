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
                        && !"abcdef".StartsWith("def")
                        && "abcdef".EndsWith("def")
                        && !"abcdef".EndsWith("abc")
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
            using Verifiable.Contracts;

            namespace Sample;

            [Verifiable]
            public static class NullChecks
            {
                public static bool BadStartsWith(string? value)
                {
                    Spec.Ensures<bool>(r => r == value!.StartsWith("a"));
                    return value!.StartsWith("a");
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
