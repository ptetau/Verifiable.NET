using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Verifiable.Analyzers.Smt;
using Xunit;

namespace Verifiable.Analyzers.Tests;

/// <summary>Regression tests for issue #32: the regex model must agree with what .NET's Regex does.</summary>
public class RegexModelDotNetParityTests
{
    private static bool InTable(int[] ranges, int ch)
    {
        for (var i = 0; i < ranges.Length; i += 2)
        {
            if (ch >= ranges[i] && ch <= ranges[i + 1]) return true;
        }
        return false;
    }

    [Fact]
    public void UnicodeTables_MatchDotNetsDigitAndWordClasses()
    {
        var digit = new Regex(@"^\d\z");
        var word = new Regex(@"^\w\z");
        var drift = new List<string>();
        for (var i = 0; i <= 0xFFFF; i++)
        {
            var s = ((char)i).ToString();
            if (digit.IsMatch(s) != InTable(RegexUnicodeTables.DigitRanges, i)) drift.Add($"\\d U+{i:X4}");
            if (word.IsMatch(s) != InTable(RegexUnicodeTables.WordRanges, i)) drift.Add($"\\w U+{i:X4}");
        }
        Assert.True(drift.Count == 0, "Regenerate RegexUnicodeTables; the runtime's Unicode data differs: " + string.Join(", ", drift.Take(10)));
    }

    [Theory]
    [InlineData(256, 16)]   // ECMAScript | Singleline
    [InlineData(256, 4)]    // ECMAScript | ExplicitCapture
    [InlineData(256, 1024)] // ECMAScript | NonBacktracking
    public void EcmaScriptWithUnsupportedPartner_IsRejected_AsDotNetRejectsIt(int a, int b)
    {
        var options = a | b;
        Assert.Throws<ArgumentOutOfRangeException>(() => new Regex("a", (RegexOptions)options));
        Assert.False(RegexModel.TryTranslate("a", options, out _, out var error));
        Assert.Contains("ECMAScript", error);
    }

    [Theory]
    [InlineData(256 | 8)]
    [InlineData(256 | 512)]
    [InlineData(16 | 4 | 8)]
    [InlineData(1024 | 16)]
    public void ValidOptionCombinations_AreStillAccepted(int options)
    {
        _ = new Regex("a", (RegexOptions)options);
        Assert.True(RegexModel.TryTranslate("a", options, out _, out _));
    }

    // Every pattern the model accepts must be accepted by the Regex constructor too.
    [Theory]
    [InlineData(@"^\_\z", 0)]
    [InlineData(@"^[\_]\z", 0)]
    [InlineData(@"^\é\z", 0)]
    [InlineData("^\\‍\\z", 0)]
    [InlineData(@"^\u 41 \z", 0)]
    [InlineData(@"^\x 4\z", 0)]
    [InlineData(@"^\u+041\z", 0)]
    // issue #53: stacked quantifiers and a quantifier with nothing to repeat
    [InlineData(@"^a{2}{3}\z", 0)]
    [InlineData(@"^a*{2}\z", 0)]
    [InlineData(@"^a{2}*\z", 0)]
    [InlineData(@"^a{2}+\z", 0)]
    [InlineData(@"^a{2}??\z", 0)]
    [InlineData(@"^a*?{3}\z", 0)]
    [InlineData(@"^a{1,2}{3,4}\z", 0)]
    [InlineData(@"^a+{2}\z", 0)]
    [InlineData(@"{2}a", 0)]
    [InlineData(@"^(?:{2}a)\z", 0)]
    [InlineData(@"^(a|{2}b)\z", 0)]
    [InlineData(@"^a|{2}b", 0)]
    [InlineData(@"{1}", 0)]
    public void PatternsDotNetRejects_AreRejectedByTheModel(string pattern, int options)
    {
        Assert.ThrowsAny<ArgumentException>(() => new Regex(pattern, (RegexOptions)options));
        Assert.False(RegexModel.TryTranslate(pattern, options, out _, out _), pattern);
    }

    [Theory]
    [InlineData(@"^\_\z", 256)]
    [InlineData(@"^[\_]\z", 256)]
    [InlineData(@"^\.\z", 0)]
    [InlineData(@"^\-\z", 0)]
    [InlineData(@"^\012\z", 0)]
    [InlineData(@"^\0123\z", 0)]
    [InlineData(@"^[\012-\015]\z", 0)]
    // issue #53: a '{' that does not form a quantifier is literal, also after a quantifier
    [InlineData(@"^a{,3}\z", 0)]
    [InlineData(@"^a{x}\z", 0)]
    [InlineData(@"^a{2}{x}\z", 0)]
    [InlineData(@"^a{2}{,3}\z", 0)]
    [InlineData(@"^a*{\z", 0)]
    [InlineData(@"^{\z", 0)]
    [InlineData(@"^{,3}\z", 0)]
    [InlineData(@"^a{2}?\z", 0)]
    [InlineData(@"^(?:){2}a\z", 0)]
    public void PatternsDotNetAccepts_AreStillAccepted(string pattern, int options)
    {
        _ = new Regex(pattern, (RegexOptions)options);
        Assert.True(RegexModel.TryTranslate(pattern, options, out _, out var error), error);
    }

    // A quantifier on a zero-width anchor is valid in .NET but not modelled, so the model must refuse it.
    [Theory]
    [InlineData(@"^{2}a\z")]
    [InlineData(@"^{2}")]
    [InlineData(@"\A{2}a")]
    [InlineData(@"^*a")]
    [InlineData(@"^?a")]
    [InlineData(@"\A+a")]
    [InlineData(@"a$*")]
    [InlineData(@"a$?")]
    [InlineData(@"a\z{2}")]
    [InlineData(@"a$+")]
    [InlineData(@"a\Z*")]
    public void QuantifiedAnchors_AreRejectedByTheModel(string pattern)
    {
        _ = new Regex(pattern);
        Assert.False(RegexModel.TryTranslate(pattern, 0, out _, out _), pattern);
    }

    [Fact]
    public void DigitAndWordClasses_AreAsciiOnlyUnderEcmaScript_AndUnicodeOtherwise()
    {
        Assert.True(RegexModel.TryTranslate(@"^\d\z", 256, out var asciiDigit, out _));
        Assert.Contains("(re.range \"0\" \"9\")", asciiDigit);
        Assert.DoesNotContain("0660", asciiDigit);

        Assert.True(RegexModel.TryTranslate(@"^\d\z", 0, out var uniDigit, out _));
        Assert.Contains("\\u{660}", uniDigit);

        Assert.True(RegexModel.TryTranslate(@"^\w\z", 256, out var asciiWord, out _));
        Assert.DoesNotContain("\\u{e9}", asciiWord);
        Assert.True(RegexModel.TryTranslate(@"^\w\z", 0, out var uniWord, out _));
        Assert.Contains("\\u{c0}", uniWord);
    }

    private static async Task<Verdict> Analyze(string source)
    {
        var diags = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableGatekeeperAnalyzer(), new VerifiableSmtAnalyzer());
        Assert.DoesNotContain(diags, d => d.Id == "AD0001" || d.Id == "VER104");
        var errors = diags.Where(d => d.Id.StartsWith("VER", StringComparison.Ordinal) && d.Severity == DiagnosticSeverity.Error).ToList();
        return new Verdict(errors.Count > 0, string.Join("\n", errors.Select(d => d.Id + ": " + d.GetMessage())));
    }

    private sealed record Verdict(bool Rejected, string Messages);

    private static string Program(string body) => """
        using System.Text.RegularExpressions;
        using Verifiable.Contracts;

        namespace Sample;

        [Verifiable]
        public static class T
        {
        """ + "\n" + body + "\n}\n";

    [Theory]
    // \d / \w without ECMAScript match non-ASCII characters (ARABIC-INDIC DIGIT THREE, e-acute)
    [InlineData("""
        public static bool M(string s)
        {
            Spec.Requires(s == "٣");
            Spec.Ensures<bool>(r => r);
            return Regex.IsMatch(s, @"^\d\z");
        }
        """, false)]
    [InlineData("""
        public static bool M(string s)
        {
            Spec.Requires(s == "é");
            Spec.Ensures<bool>(r => r);
            return Regex.IsMatch(s, @"^\w\z");
        }
        """, false)]
    [InlineData("""
        public static bool M(string s)
        {
            Spec.Requires(s == "\u00e9" || s == "\u0663");
            Spec.Ensures<bool>(r => r && !Regex.IsMatch(s, @"^\W\z"));
            return Regex.IsMatch(s, @"^[\d\w]\z");
        }
        """, false)]
    // Under ECMAScript they stay ASCII
    [InlineData("""
        public static bool M(string s)
        {
            Spec.Requires(s == "٣");
            Spec.Ensures<bool>(r => !r);
            return Regex.IsMatch(s, @"^\d\z", RegexOptions.ECMAScript);
        }
        """, false)]
    [InlineData("""
        public static bool M(string s)
        {
            Spec.Requires(s != null);
            Spec.Ensures<bool>(r => r == Regex.IsMatch(s, @"^[0-9A-Za-z_]\z"));
            return Regex.IsMatch(s, @"^\w\z", RegexOptions.ECMAScript);
        }
        """, false)]
    // The claim that \d is ASCII is wrong without ECMAScript
    [InlineData("""
        public static bool M(string s)
        {
            Spec.Requires(s != null);
            Spec.Ensures<bool>(r => !r || (s[0] >= '0' && s[0] <= '9'));
            return Regex.IsMatch(s, @"^\d\z");
        }
        """, true)]
    // Octal escapes: \012 is one newline; \0123 is newline then '3'
    [InlineData("""
        public static bool M(string s)
        {
            Spec.Requires(s != null);
            Spec.Ensures<bool>(r => r == (s == "\n"));
            return Regex.IsMatch(s, @"^\012\z");
        }
        """, false)]
    [InlineData("""
        public static bool M(string s)
        {
            Spec.Requires(s != null);
            Spec.Ensures<bool>(r => r == (s == "\n3"));
            return Regex.IsMatch(s, @"^\0123\z");
        }
        """, false)]
    [InlineData("""
        public static bool M(string s)
        {
            Spec.Requires(s != null);
            Spec.Ensures<bool>(r => r == (s == "\0"));
            return Regex.IsMatch(s, @"^\0\z");
        }
        """, false)]
    [InlineData("""
        public static bool M(string s)
        {
            Spec.Requires(s != null);
            Spec.Ensures<bool>(r => !r || s.Length == 3);
            return Regex.IsMatch(s, @"^\012\z");
        }
        """, true)]
    // \_ throws at run time
    [InlineData("""
        public static bool M(string s)
        {
            Spec.Requires(s != null);
            Spec.Ensures<bool>(r => r == (s == "_"));
            return Regex.IsMatch(s, @"^\_\z");
        }
        """, true)]
    [InlineData("""
        public static bool M(string s)
        {
            Spec.Requires(s != null);
            Spec.Ensures<bool>(r => r == (s == "_"));
            return Regex.IsMatch(s, @"^\_\z", RegexOptions.ECMAScript);
        }
        """, false)]
    // Invalid option combinations throw at run time
    [InlineData("""
        public static bool M(string s)
        {
            Spec.Requires(s != null);
            Spec.Ensures<bool>(r => r == (s == "a"));
            return Regex.IsMatch(s, @"^a\z", RegexOptions.ECMAScript | RegexOptions.Singleline);
        }
        """, true)]
    [InlineData("""
        public static bool M(string s)
        {
            Spec.Requires(s != null);
            Spec.Ensures<bool>(r => r == (s == "a"));
            return Regex.IsMatch(s, @"^a\z", RegexOptions.ECMAScript | RegexOptions.Multiline);
        }
        """, true)]
    // A string is made of UTF-16 code units, so a one-unit string is always in the full BMP range
    [InlineData("""
        public static bool M(string s)
        {
            Spec.Requires(s != null && s.Length == 1);
            Spec.Ensures<bool>(r => r);
            return Regex.IsMatch(s, @"^[\u0000-￿]\z");
        }
        """, false)]
    public async Task Verdict_MatchesDotNet(string body, bool rejected)
    {
        var result = await Analyze(Program(body));
        Assert.True(rejected == result.Rejected, $"expected rejected={rejected} for:\n{body}\n{result.Messages}");
    }

    private static string FieldProgram(string patternsClass) => """
        using System.Text.RegularExpressions;
        using Verifiable.Contracts;

        namespace Sample;

        """ + patternsClass + """

        [Verifiable]
        public static class T
        {
            public static bool IsA(string s)
            {
                Spec.Requires(s != null);
                Spec.Ensures<bool>(r => r == (s == "a"));
                return Patterns.OnlyA.IsMatch(s);
            }
        }
        """;

    [Theory]
    // initializer only: trusted
    [InlineData("public static class Patterns { public static readonly Regex OnlyA = new Regex(\"^a\\\\z\"); }", false)]
    // a static constructor that does not touch the field: trusted
    [InlineData("public static class Patterns { public static readonly Regex OnlyA = new Regex(\"^a\\\\z\"); public static readonly int N; static Patterns() { N = 3; } }", false)]
    // a static constructor that replaces it: not trusted
    [InlineData("public static class Patterns { public static readonly Regex OnlyA = new Regex(\"^a\\\\z\"); static Patterns() { OnlyA = new Regex(\"^b\\\\z\"); } }", true)]
    [InlineData("public static class Patterns { public static readonly Regex OnlyA = new Regex(\"^a\\\\z\"); static Patterns() { Patterns.OnlyA = new Regex(\"^b\\\\z\"); } }", true)]
    [InlineData("public static class Patterns { public static readonly Regex OnlyA = new Regex(\"^a\\\\z\"); static void Set(out Regex r) { r = null!; } static Patterns() { Set(out OnlyA); } }", true)]
    // a static initializer that passes it by reference: not trusted
    [InlineData("public static class Patterns { public static readonly Regex OnlyA = new Regex(\"^a\\\\z\"); static Regex Other = Swap(ref OnlyA); static Regex Swap(ref Regex r) { return r; } }", true)]
    // a harmless static initializer that only reads it: trusted
    [InlineData("public static class Patterns { public static readonly Regex OnlyA = new Regex(\"^a\\\\z\"); public static readonly bool Ok = OnlyA.IsMatch(\"a\"); }", false)]
    // a partial type whose other part reassigns it: not trusted
    [InlineData("public static partial class Patterns { public static readonly Regex OnlyA = new Regex(\"^a\\\\z\"); } public static partial class Patterns { static Patterns() { OnlyA = new Regex(\"^b\\\\z\"); } }", true)]
    public async Task StaticReadonlyRegexField_IsTrustedOnlyWhenNothingReassignsIt(string patternsClass, bool rejected)
    {
        var result = await Analyze(FieldProgram(patternsClass));
        Assert.True(rejected == result.Rejected, $"expected rejected={rejected} for:\n{patternsClass}\n{result.Messages}");
    }
}
