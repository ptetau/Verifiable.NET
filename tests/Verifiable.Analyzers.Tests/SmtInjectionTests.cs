using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Verifiable.Analyzers.Smt;
using Xunit;

namespace Verifiable.Analyzers.Tests;

/// <summary>
/// User text reaches the SMT script as string literals and as identifiers. These tests try to break out of
/// both. Each case checks that a true contract is still proved and a false one is still refuted: an injected
/// <c>(assert false)</c> would prove everything, and a broken symbol would leave a true contract undecided.
/// </summary>
public class SmtInjectionTests
{
    public static TheoryData<string> HostileStrings => new()
    {
        "a\"b",
        "\"",
        "\\",
        "C:\\",
        "\\\"",
        "\\u{41}",
        "(",
        ")",
        ")) (assert false) ((",
        "\")) (assert false) ((\"",
        "\"\")) (assert false) ((\"\"",
        "(echo \"__VERIFIABLE_DONE__\")",
        "__VERIFIABLE_DONE__",
        "__VERIFIABLE_MODEL_DONE__",
        "\nunsat\n",
        "line1\r\nunsat\r\n__VERIFIABLE_DONE__",
        "; (assert false)",
        "|x|",
        "\0",
        "\u00e9\u0301",
        "\u3000\u00a0",
        "\ud83d\ude00",
        "\ud800",
        "\udfff\ud800",
    };

    [Theory]
    [MemberData(nameof(HostileStrings))]
    public async Task StringLiteral_TrueContractIsProved(string value)
    {
        var lit = CSharpLiteral(value);
        await AssertProvedAsync($$"""
            public static bool Same(string s)
            {
                Spec.Requires(s == {{lit}}, "pinned");
                Spec.Ensures<bool>(r => r, "the pinned value is what it is");
                return s.Length == {{value.Length}} && s == {{lit}} && s != {{lit}} + "x";
            }

            public static string Echo()
            {
                Spec.Ensures<string>(r => r == {{lit}} && r.Length == {{value.Length}}, "returns the literal");
                return {{lit}};
            }
            """);
    }

    [Theory]
    [MemberData(nameof(HostileStrings))]
    public async Task StringLiteral_FalseContractIsRefuted(string value)
    {
        var lit = CSharpLiteral(value);
        await AssertRefutedAsync($$"""
            public static bool Differs(string s)
            {
                Spec.Requires(s == {{lit}}, "pinned");
                Spec.Ensures<bool>(r => r, "claims the pinned value differs from itself");
                return s != {{lit}};
            }
            """);
        await AssertRefutedAsync($$"""
            public static string Echo()
            {
                Spec.Ensures<string>(r => r == {{lit}}, "claims an extra character is not there");
                return {{lit}} + "x";
            }
            """);
    }

    [Fact]
    public async Task CounterexampleThatSpellsTheSentinel_IsReportedInFull()
    {
        var diags = await AnalyzeAsync("""
            public static int Count(string s, int n)
            {
                Spec.Requires(s == "__VERIFIABLE_DONE__" && n == 7, "pinned");
                Spec.Ensures<int>(r => r > 100, "false on purpose");
                return n;
            }
            """);

        var refuted = Assert.Single(diags, d => d.Id == "VER102");
        var message = refuted.GetMessage(CultureInfo.InvariantCulture);
        Assert.Contains("__VERIFIABLE_DONE__", message);
        Assert.Contains("n = 7", message);
    }

    [Fact]
    public async Task CounterexampleEndingInBackslash_KeepsTheValuesAfterIt()
    {
        // Z3 prints "C:\" with a bare backslash before the closing quote. Reading that backslash as an escape
        // swallowed the rest of the model, so `n` went missing from the counterexample.
        var diags = await AnalyzeAsync("""
            public static int Count(int n, string s)
            {
                Spec.Requires(s == "C:\\" && n == 9, "pinned");
                Spec.Ensures<int>(r => r > 100, "false on purpose");
                return n;
            }
            """);

        var message = Assert.Single(diags, d => d.Id == "VER102").GetMessage(CultureInfo.InvariantCulture);
        Assert.Contains("n = 9", message);
    }

    public static TheoryData<string, string> HostileIdentifiers => new()
    {
        // Parameters whose names collided once every non-alphanumeric character became '_'.
        {
            "public static int F(int a_b, int a\u203fb) { Spec.Requires(a_b == 0 && a\u203fb == 1, \"pinned\"); Spec.Ensures<int>(r => r == 1, \"returns the second\"); return a\u203fb; }",
            "public static int F(int a_b, int a\u203fb) { Spec.Requires(a_b == 0, \"pinned\"); Spec.Ensures<int>(r => r == 0, \"claims the second is the first\"); return a\u203fb; }"
        },
        {
            "public static int F(int e_, int e\u0301) { Spec.Requires(e_ == 0 && e\u0301 == 1, \"pinned\"); Spec.Ensures<int>(r => r == 1, \"returns the second\"); return e\u0301; }",
            "public static int F(int e_, int e\u0301) { Spec.Requires(e_ == 0, \"pinned\"); Spec.Ensures<int>(r => r == 0, \"claims the second is the first\"); return e\u0301; }"
        },
        // Non-ASCII names: Z3 rejects non-ASCII symbol characters.
        {
            "public static int F(int \u00e4) { Spec.Requires(\u00e4 > 0 && \u00e4 <= 1000, \"positive\"); var \u00f6 = \u00e4 + 1; Spec.Ensures<int>(r => r > 1, \"grows\"); return \u00f6; }",
            "public static int F(int \u00e4) { Spec.Requires(\u00e4 > 0 && \u00e4 <= 1000, \"positive\"); var \u00f6 = \u00e4 + 1; Spec.Ensures<int>(r => r > 2, \"false on purpose\"); return \u00f6; }"
        },
        {
            "public static int F(int \u0394x) { Spec.Requires(\u0394x >= -1000 && \u0394x <= 1000, \"small\"); var \u5024 = \u0394x * 2; Spec.Ensures<int>(r => r == \u0394x + \u0394x, \"doubles\"); return \u5024; }",
            "public static int F(int \u0394x) { Spec.Requires(\u0394x >= -1000 && \u0394x <= 1000, \"small\"); var \u5024 = \u0394x * 2; Spec.Ensures<int>(r => r == \u0394x + 1, \"false on purpose\"); return \u5024; }"
        },
        // Verbatim identifiers and names that are SMT keywords or look like the lowerer's own names.
        {
            "public static int F(int @class, int @if, int assert, int ite, int let, int forall) { Spec.Requires(@class > 0 && @class <= 1000 && @if > 0 && @if <= 1000 && assert >= 0 && assert <= 1000 && ite >= 0 && ite <= 1000 && let >= 0 && let <= 1000 && forall >= 0 && forall <= 1000, \"positive\"); Spec.Ensures<int>(r => r > 0, \"sum of positives\"); return @class + @if + 0 * (assert + ite + let + forall); }",
            "public static int F(int @class, int @if, int assert, int ite, int let, int forall) { Spec.Requires(@class > 0 && @class <= 1000 && @if > 0 && @if <= 1000 && assert >= 0 && assert <= 1000 && ite >= 0 && ite <= 1000 && let >= 0 && let <= 1000 && forall >= 0 && forall <= 1000, \"positive\"); Spec.Ensures<int>(r => r > @class + @if, \"false on purpose\"); return @class + @if + 0 * (assert + ite + let + forall); }"
        },
        {
            "public static int F(int p_x, int ssa_1_x, int x) { Spec.Requires(x == 1 && p_x == 2 && ssa_1_x == 3, \"pinned\"); var p_y = x + p_x; var ssa_2_y = p_y + ssa_1_x; Spec.Ensures<int>(r => r == 6, \"sums\"); return ssa_2_y; }",
            "public static int F(int p_x, int ssa_1_x, int x) { Spec.Requires(x == 1 && p_x == 2 && ssa_1_x == 3, \"pinned\"); var p_y = x + p_x; var ssa_2_y = p_y + ssa_1_x; Spec.Ensures<int>(r => r == 5, \"false on purpose\"); return ssa_2_y; }"
        },
        {
            "public static int F(int path_1, int assume_result_1, int query_negated_goal) { Spec.Requires(path_1 > 0, \"positive\"); Spec.Ensures<int>(r => r > 0, \"positive\"); return path_1; }",
            "public static int F(int path_1, int assume_result_1, int query_negated_goal) { Spec.Requires(path_1 > 0, \"positive\"); Spec.Ensures<int>(r => r > path_1, \"false on purpose\"); return path_1; }"
        },
    };

    [Theory]
    [MemberData(nameof(HostileIdentifiers))]
    public async Task Identifier_TrueContractIsProved_FalseContractIsRefuted(string trueMember, string falseMember)
    {
        await AssertProvedAsync(trueMember);
        await AssertRefutedAsync(falseMember);
    }

    [Fact]
    public async Task ParameterNamedThis_DoesNotClashWithTheReceiver()
    {
        const string type = """
            [Verifiable]
            public readonly record struct Box(int V)
            {
                public int Pick(Box @this)
                {
                    Spec.Requires(V == 1 && @this.V == 2, "pinned");
                    Spec.Ensures<int>(r => r == RESULT, "picks the parameter");
                    return @this.V;
                }
            }
            """;

        await AssertNoErrorsAsync(Wrap(type.Replace("RESULT", "2")));
        var diags = await AnalyzeSourceAsync(Wrap(type.Replace("RESULT", "1")));
        Assert.Contains(diags, d => d.Id == "VER102");
    }

    [Fact]
    public async Task RecordFieldsThatSanitizedAlike_StayDistinct()
    {
        const string type = """
            [Verifiable]
            public readonly record struct Pair(int a_b, int a‿b);

            [Verifiable]
            public static class Use
            {
                public static int Second(Pair p)
                {
                    Spec.Requires(p.a_b == 0 && p.a‿b == 1, "pinned");
                    Spec.Ensures<int>(r => r == RESULT, "reads the second field");
                    return p.a‿b;
                }
            }
            """;

        await AssertNoErrorsAsync(Wrap(type.Replace("RESULT", "1")));
        var diags = await AnalyzeSourceAsync(Wrap(type.Replace("RESULT", "0")));
        Assert.Contains(diags, d => d.Id == "VER102");
    }

    [Theory]
    [InlineData("(str-val \"a\")", "\"a\"")]
    [InlineData("(str-val (str.++ \"a\" p))", "(str.++ \"a\" p)")]
    [InlineData("(str-val \")\")", "\")\"")]
    [InlineData("(str-val \"a\"\")(\")", "\"a\"\")(\"")]
    [InlineData("(str-val \"\\\")", "\"\\\"")]
    public void TextOfAStrValTerm_IsItsArgument(string term, string text)
    {
        Assert.Equal(text, CfgSsaLowerer.TextOrEmptyIfNull(term));
    }

    [Theory]
    [InlineData("p_s")]
    [InlineData("(str-val \"a\") ")]
    [InlineData("(str-val \"a\")(str-val \"b\")")]
    [InlineData("(str-val \"a)\" x)) (assert false) ((str-val \"b\")")]
    [InlineData("(ite c (str-val \"a\") p_s)")]
    public void TextOfAnyOtherTerm_ChecksForNull(string term)
    {
        Assert.Equal($"(ite (= {term} str-null) \"\" (str-text {term}))", CfgSsaLowerer.TextOrEmptyIfNull(term));
    }

    [Fact]
    public async Task CharToString_IsTheOneCharacterString()
    {
        await AssertProvedAsync("""
            public static string Show(char c)
            {
                Spec.Requires(c == 'q', "pinned");
                Spec.Ensures<string>(r => r == "q" && r.Length == 1, "one character");
                return c.ToString();
            }

            public static string High(int n)
            {
                Spec.Requires(n == 65536 + 34, "pinned");
                Spec.Ensures<string>(r => r == "\u0001\"", "two code units");
                return ((char)(n / 65536)).ToString() + ((char)(n % 65536)).ToString();
            }
            """);
        await AssertRefutedAsync("""
            public static string Show(char c)
            {
                Spec.Ensures<string>(r => r == "q", "false on purpose");
                return c.ToString();
            }
            """);
    }

    [Fact]
    public void SymbolPart_IsAsciiAndOneToOne()
    {
        var names = new[] { "a_b", "a\u203fb", "a$203fb", "e_", "e\u0301", "\u00e4", "_00e4", "x", "X", "", "$", "this", "p_x" };
        var parts = names.Select(SmtTypeSystem.SymbolPart).ToList();

        Assert.All(parts, p => Assert.All(p, ch => Assert.True(ch < 128 && (char.IsLetterOrDigit(ch) || ch is '_' or '$'), p)));
        Assert.Equal(parts.Count, parts.Distinct().Count());
        Assert.Equal("a_b", SmtTypeSystem.SymbolPart("a_b"));
        Assert.Equal("a$203fb", SmtTypeSystem.SymbolPart("a\u203fb"));
    }

    private static string Wrap(string types) => "using Verifiable.Contracts;\n" + types;

    private static string WrapMember(string member) =>
        "using Verifiable.Contracts;\n[Verifiable] public static class Hostile\n{\n" + member + "\n}\n";

    private static Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string member) => AnalyzeSourceAsync(WrapMember(member));

    private static Task<ImmutableArray<Diagnostic>> AnalyzeSourceAsync(string source) =>
        AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableGatekeeperAnalyzer(), new VerifiableSmtAnalyzer());

    private static async Task AssertProvedAsync(string member) => await AssertNoErrorsAsync(WrapMember(member));

    private static async Task AssertNoErrorsAsync(string source)
    {
        var diags = await AnalyzeSourceAsync(source);
        Assert.DoesNotContain(diags, d => d.Id == "VER104");
        Assert.Empty(diags.Where(d => d.Severity == DiagnosticSeverity.Error || d.Id == "AD0001")
            .Select(d => d.ToString()));
    }

    private static async Task AssertRefutedAsync(string member)
    {
        var diags = await AnalyzeAsync(member);
        Assert.DoesNotContain(diags, d => d.Id is "VER104" or "VER105" or "AD0001");
        Assert.DoesNotContain(diags, d => d.Severity == DiagnosticSeverity.Error && d.Id.StartsWith("CS", System.StringComparison.Ordinal));
        Assert.Contains(diags, d => d.Id == "VER102");
    }

    /// <summary>A C# regular string literal for <paramref name="value"/>, escaping everything but printable ASCII.</summary>
    private static string CSharpLiteral(string value)
    {
        var sb = new StringBuilder("\"");
        foreach (var ch in value)
        {
            if (ch == '"') sb.Append("\\\"");
            else if (ch == '\\') sb.Append("\\\\");
            else if (ch >= 0x20 && ch <= 0x7e) sb.Append(ch);
            else sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
        }

        return sb.Append('"').ToString();
    }
}
