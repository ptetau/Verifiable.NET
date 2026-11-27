using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Verifiable.Analyzers.Smt;
using Xunit;

namespace Verifiable.Analyzers.Tests;

public class RegexModelTests
{
    [Theory]
    [InlineData(@"^\d{5}(-\d{4})?$", "١٢٣٤٥-6789\n")]
    [InlineData(@"^\d{5}(-\d{4})?$", "12345\r\n")]
    [InlineData(@"^\d{5}(-\d{4})?$", "12345\n\n")]
    [InlineData(@"^\d$", "𝟘")] // UTF-16 surrogate pair, not a single digit
    [InlineData(@"^[0-9]$", "١")]
    [InlineData(@"^[a-z]|[0-9]$", "XYZ9\n")]
    [InlineData(@"^[a-z]|[0-9]$", "XYZ9x")]
    [InlineData(@"foo(?:bar|qux)+", "\nfoobarqux!")]
    [InlineData(@"\Afoo\z", "foo\n")]
    [InlineData(@"^\\u\{41\}\Z", @"\u{41}")]
    [InlineData(@"^(a{2,3}|[""\t])*$", "aa\t\"aaa")]
    public void MembershipAgreesWithDotNet(string pattern, string input)
    {
        var match = $"(str.in_re {ContractExpressionTranslator.EscapeSmtString(input)} {RegexModel.Translate(pattern)})";
        var result = Z3SolverRunner.Execute(Regex.IsMatch(input, pattern) ? $"(assert (not {match}))" : $"(assert {match})");
        Assert.Equal(SmtStatus.Unsat, result.Status);
    }

    [Theory]
    [InlineData(@"(?:^a|b)$")]
    [InlineData(@"a$b")]
    [InlineData(@"(?=a)b")]
    [InlineData(@"(a)\1")]
    [InlineData(@"(?i:a)")]
    [InlineData(@"a*?")]
    [InlineData(@"[a-z-[b]]")]
    [InlineData(@"[z-a]")]
    [InlineData(@"\p{Nd}")]
    [InlineData(@"(a")]
    [InlineData(@"a{999999999999999999999}")]
    [InlineData(@"(a{128}){128}")]
    public void UnsupportedPatternsAreRejected(string pattern) =>
        Assert.Throws<ContractLoweringException>(() => RegexModel.Translate(pattern));

    [Fact]
    public void ParserBoundsSizeAndRecursion()
    {
        Assert.Throws<ContractLoweringException>(() => RegexModel.Translate(new string('a', 257)));
        Assert.Throws<ContractLoweringException>(() => RegexModel.Translate(new string('(', 32) + new string(')', 32)));
    }

    [Fact]
    public async Task GeneratedAndStaticPostalValidatorsProveEquivalent()
    {
        const string source = """
            using System.Text.RegularExpressions;
            using Verifiable.Contracts;
            public static partial class Patterns
            {
                public const string Postal = @"^\d{5}(-\d{4})?$";
                [GeneratedRegex(Postal)]
                public static partial Regex PostalRegex();
            }
            [Verifiable]
            public static class Validator
            {
                public static bool Check(string? value)
                {
                    Spec.Ensures<bool>(r => r == (value != null && Regex.IsMatch(
                        options: RegexOptions.None, pattern: Patterns.Postal, input: value)));
                    return value != null && Patterns.PostalRegex().IsMatch(value);
                }
            }
            """;
        var assembly = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "System.Text.RegularExpressions.Generator.dll"));
        var generator = (IIncrementalGenerator)Activator.CreateInstance(
            assembly.GetType("System.Text.RegularExpressions.Generator.RegexGenerator", throwOnError: true)!)!;
        var ds = await AnalyzerTestHarness.AnalyzeWithGeneratorAsync(source, null, [typeof(Regex)], generator.AsSourceGenerator(),
            new VerifiableGatekeeperAnalyzer(), new VerifiableSmtAnalyzer());
        Assert.Empty(ds.Where(d => d.Id == "AD0001" || d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public async Task HandwrittenFactoryCannotProveItsAdvertisedPattern()
    {
        const string source = """
            using System.Text.RegularExpressions;
            using Verifiable.Contracts;
            public static partial class Patterns
            {
                [GeneratedRegex("^x$")]
                public static partial Regex X();
                [System.CodeDom.Compiler.GeneratedCode("System.Text.RegularExpressions.Generator", "10.0")]
                public static partial Regex X() => new Regex("^y$");
            }
            [Verifiable] public static class C
            {
                public static bool Check()
                {
                    Spec.Ensures<bool>(r => r);
                    return Patterns.X().IsMatch("x");
                }
            }
            """;
        var ds = await AnalyzerTestHarness.AnalyzeWithReferencesAsync(source, null, [typeof(Regex)],
            new VerifiableGatekeeperAnalyzer(), new VerifiableSmtAnalyzer());
        Assert.DoesNotContain(ds, d => d.Id == "AD0001" || d.Id.StartsWith("CS", StringComparison.Ordinal) && d.Severity == DiagnosticSeverity.Error);
        Assert.Contains(ds, d => d.Id == "VER006");
        Assert.Contains(ds, d => d.Id == "VER101");
    }

    [Theory]
    [InlineData("return Regex.IsMatch(value!, \"^x$\");")]
    [InlineData("Spec.Ensures<bool>(r => r == Regex.IsMatch(value!, \"\")); return true;")]
    public async Task UnguardedNullableInputFailsInBodyOrContract(string body)
    {
        var source = $$"""
            using System.Text.RegularExpressions;
            using Verifiable.Contracts;
            [Verifiable] public static class C
            {
                public static bool Check(string? value) { {{body}} }
            }
            """;
        _ = typeof(Regex).Assembly;
        var ds = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableSmtAnalyzer());
        Assert.Contains(ds, d => d.Id == "VER102" && d.GetMessage().Contains("must not be null"));
    }

    [Theory]
    [InlineData("Regex.IsMatch(\"A\", pattern)")]
    [InlineData("Regex.IsMatch(\"A\", \"a\", RegexOptions.IgnoreCase)")]
    [InlineData("Regex.IsMatch(\"A\", \"a\", RegexOptions.None, System.TimeSpan.Zero)")]
    [InlineData("new Regex(\"A\").IsMatch(\"A\")")]
    public async Task UnsupportedCallsStayOutsideBothModels(string call)
    {
        var source = $$"""
            using System.Text.RegularExpressions;
            using Verifiable.Contracts;
            [Verifiable] public static class C
            {
                public static bool Check(string pattern)
                {
                    Spec.Ensures<bool>(r => r == {{call}});
                    return {{call}};
                }
            }
            """;
        _ = typeof(Regex).Assembly;
        var ds = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableGatekeeperAnalyzer(), new VerifiableSmtAnalyzer());
        Assert.DoesNotContain(ds, d => d.Id == "AD0001" || d.Id.StartsWith("CS", StringComparison.Ordinal) && d.Severity == DiagnosticSeverity.Error);
        Assert.Contains(ds, d => d.Id == "VER006");
        Assert.Contains(ds, d => d.Id == "VER101");
    }
}
