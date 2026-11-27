using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Verifiable.Analyzers.Smt;
using Xunit;

namespace Verifiable.Analyzers.Tests;

public class RegexModelTests
{
    private static IEnumerable<ISourceGenerator> LoadSdkRegexGenerators()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "System.Text.RegularExpressions.Generator.dll");
        if (!File.Exists(path))
        {
            return Array.Empty<ISourceGenerator>();
        }

        var asm = Assembly.LoadFrom(path);
        var list = new List<ISourceGenerator>();
        Type?[] types;
        try
        {
            types = asm.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types;
        }
        foreach (var type in types)
        {
            if (type is null || type.IsAbstract || type.IsInterface) continue;
            if (typeof(IIncrementalGenerator).IsAssignableFrom(type) && type.GetConstructor(Type.EmptyTypes) is not null)
            {
                list.Add(((IIncrementalGenerator)Activator.CreateInstance(type)!).AsSourceGenerator());
            }
            else if (typeof(ISourceGenerator).IsAssignableFrom(type) && type.GetConstructor(Type.EmptyTypes) is not null)
            {
                list.Add((ISourceGenerator)Activator.CreateInstance(type)!);
            }
        }
        return list;
    }

    [Fact]
    public async Task D18_GeneratedRegexPostalValidator_VerifiesEndToEnd()
    {
        const string source = """
            using System.Text.RegularExpressions;
            using Verifiable.Contracts;

            namespace Sample;

            [Verifiable]
            public static partial class CodeValidator
            {
                [GeneratedRegex(@"^\d{5}(-\d{4})?$")]
                private static partial Regex PostalRegex();

                public static bool IsValidPostalCode(string? value)
                {
                    Spec.Ensures<bool>(r => r == (value != null && PostalRegex().IsMatch(value)));
                    return value != null && PostalRegex().IsMatch(value);
                }

                public static bool KnownPostalExamples()
                {
                    Spec.Ensures<bool>(r => r);
                    return IsValidPostalCode("12345")
                        && IsValidPostalCode("12345-6789")
                        && !IsValidPostalCode("1234")
                        && !IsValidPostalCode("abcde")
                        && !IsValidPostalCode(null);
                }
            }
            """;

        var generators = LoadSdkRegexGenerators().ToList();
        var diags = await AnalyzerTestHarness.AnalyzeWithOptionsAndGeneratorsAsync(
            source, null, generators, new VerifiableGatekeeperAnalyzer(), new VerifiableSmtAnalyzer());
        Assert.Empty(diags.Where(d => d.Id == "AD0001" || d.Id == "VER104" || d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public async Task StaticRegexIsMatch_AndExactEndAnchor_ProveLengthBounds()
    {
        const string source = """
            using System.Text.RegularExpressions;
            using Verifiable.Contracts;

            namespace Sample;

            [Verifiable]
            public static class SkuRules
            {
                public static bool CheckSkuLength(string? sku)
                {
                    Spec.Requires(sku != null && Regex.IsMatch(sku, @"^[A-Z]{2}-\d{3}\z"));
                    Spec.Ensures<bool>(r => r);
                    return sku!.Length == 6;
                }
            }
            """;

        var diags = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableGatekeeperAnalyzer(), new VerifiableSmtAnalyzer());
        Assert.Empty(diags.Where(d => d.Id == "AD0001" || d.Id == "VER104" || d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public async Task RegexNullCheckObligation_IsEnforcedInBothBodyAndSpecification()
    {
        // Regression: spec-only Regex.IsMatch(s!, "") must still emit non-null obligation!
        const string specNullSource = """
            using System.Text.RegularExpressions;
            using Verifiable.Contracts;

            namespace Sample;

            [Verifiable]
            public static class SpecRegexNull
            {
                public static bool BadSpec(string? s)
                {
                    Spec.Ensures<bool>(r => r == Regex.IsMatch(s!, ""));
                    return true;
                }
            }
            """;

        var diags = await AnalyzerTestHarness.AnalyzeAsync(specNullSource, new VerifiableGatekeeperAnalyzer(), new VerifiableSmtAnalyzer());
        Assert.DoesNotContain(diags, d => d.Id == "AD0001" || d.Id == "VER104");
        Assert.Contains(diags, d => d.Id == "VER102");
    }

    [Fact]
    public async Task UnsupportedRegexConstructs_FailClosedWithVER101()
    {
        const string source = """
            using System.Text.RegularExpressions;
            using Verifiable.Contracts;

            namespace Sample;

            [Verifiable]
            public static class UnsupportedRegex
            {
                public static bool Backref(string s)
                {
                    Spec.Requires(s != null);
                    Spec.Ensures<bool>(r => r == Regex.IsMatch(s, @"^(\d)\1$"));
                    return Regex.IsMatch(s, @"^(\d)\1$");
                }
            }
            """;

        var diags = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableGatekeeperAnalyzer(), new VerifiableSmtAnalyzer());
        Assert.DoesNotContain(diags, d => d.Id == "AD0001" || d.Id == "VER104");
        Assert.Contains(diags, d => d.Id == "VER101");
    }
}
