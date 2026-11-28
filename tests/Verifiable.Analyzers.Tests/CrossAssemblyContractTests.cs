using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Verifiable.Analyzers.Smt;
using Xunit;

namespace Verifiable.Analyzers.Tests;

public class CrossAssemblyContractTests
{
    [Fact]
    public async Task D7_ExactRepro_CustomPredicateFromReferencedAssembly_ProvesAndRefutes()
    {
        var libRef = AnalyzerTestHarness.CompileLibraryReference("""
            using Verifiable.Contracts;

            namespace Sample;

            [Verifiable]
            public static class Bounds
            {
                public static bool IsValid(decimal value) => value >= -100m && value <= 100m;
            }
            """, "BoundsLib");

        const string validConsumer = """
            using Verifiable.Contracts;

            namespace Consumer;

            [Verifiable]
            public static class Identity
            {
                public static decimal Keep(decimal value)
                {
                    Spec.Requires(Sample.Bounds.IsValid(value));
                    Spec.Ensures<decimal>(r => r == value && r >= -100m && r <= 100m);
                    return value;
                }
            }
            """;

        var okDiags = await AnalyzerTestHarness.AnalyzeWithReferencesAsync(
            validConsumer,
            [libRef],
            null,
            null,
            new VerifiableGatekeeperAnalyzer(),
            new VerifiableSmtAnalyzer());
        Assert.Empty(okDiags.Where(d => d.Id == "AD0001" || d.Id == "VER104" || d.Severity == DiagnosticSeverity.Error));

        const string invalidConsumer = """
            using Verifiable.Contracts;

            namespace Consumer;

            [Verifiable]
            public static class Identity
            {
                public static decimal Keep(decimal value)
                {
                    Spec.Requires(Sample.Bounds.IsValid(value));
                    Spec.Ensures<decimal>(r => r >= 0m);
                    return value;
                }
            }
            """;

        var badDiags = await AnalyzerTestHarness.AnalyzeWithReferencesAsync(
            invalidConsumer,
            [libRef],
            null,
            null,
            new VerifiableGatekeeperAnalyzer(),
            new VerifiableSmtAnalyzer());
        Assert.DoesNotContain(badDiags, d => d.Id == "AD0001" || d.Id == "VER101" || d.Id == "VER104");
        Assert.Contains(badDiags, d => d.Id == "VER102");
    }

    [Fact]
    public async Task CrossAssembly_SpecRequiresAndEnsures_AreEnforcedAndAssumedAcrossAssemblies()
    {
        var libRef = AnalyzerTestHarness.CompileLibraryReference("""
            using Verifiable.Contracts;

            namespace DomainLib;

            [Verifiable]
            public static class Pricing
            {
                private const decimal MaxDiscount = 50m;

                public static decimal ApplyDiscount(decimal price, decimal discount)
                {
                    Spec.Requires(price >= 0m && discount >= 0m && discount <= MaxDiscount, "Discount must stay within cap.");
                    Spec.Ensures<decimal>(r => r >= 0m && r <= price);
                    var net = price - discount;
                    return net < 0m ? 0m : net;
                }
            }
            """, "DomainPricingLib");

        const string validCaller = """
            using Verifiable.Contracts;

            namespace App;

            [Verifiable]
            public static class Checkout
            {
                public static decimal Compute(decimal price)
                {
                    Spec.Requires(price >= 10m);
                    Spec.Ensures<decimal>(r => r >= 0m && r <= price);
                    return DomainLib.Pricing.ApplyDiscount(price, 5m);
                }
            }
            """;

        var okDiags = await AnalyzerTestHarness.AnalyzeWithReferencesAsync(
            validCaller,
            [libRef],
            null,
            null,
            new VerifiableGatekeeperAnalyzer(),
            new VerifiableSmtAnalyzer());
        Assert.Empty(okDiags.Where(d => d.Id == "AD0001" || d.Id == "VER104" || d.Severity == DiagnosticSeverity.Error));

        // Violates cross-assembly precondition (discount = 75m > private const MaxDiscount = 50m).
        const string badCaller = """
            using Verifiable.Contracts;

            namespace App;

            [Verifiable]
            public static class Checkout
            {
                public static decimal Compute(decimal price)
                {
                    Spec.Requires(price >= 100m);
                    return DomainLib.Pricing.ApplyDiscount(price, 75m);
                }
            }
            """;

        var badDiags = await AnalyzerTestHarness.AnalyzeWithReferencesAsync(
            badCaller,
            [libRef],
            null,
            null,
            new VerifiableGatekeeperAnalyzer(),
            new VerifiableSmtAnalyzer());
        Assert.DoesNotContain(badDiags, d => d.Id == "AD0001" || d.Id == "VER101" || d.Id == "VER104");
        Assert.Contains(badDiags, d => d.Id == "VER102" && d.GetMessage().Contains("Discount must stay within cap."));
    }

    [Fact]
    public async Task CrossAssembly_TypeAliasInvariantComputedPropertyAndInstanceMethod_VerifyAcrossAssemblies()
    {
        var libRef = AnalyzerTestHarness.CompileLibraryReference("""
            using Verifiable.Contracts;

            namespace SharedDomain;

            [TypeAlias<int>("Quantities must be positive.")]
            public readonly partial record struct PositiveQty
            {
                public static bool IsValid(int value) => value >= 1;
            }

            [Verifiable]
            public readonly record struct MoneyRange(decimal Min, decimal Max)
            {
                [Invariant("Range minimum cannot exceed maximum.")]
                public bool IsOrdered => Min <= Max;

                public decimal Span => Max - Min;

                public bool Contains(decimal amount) => amount >= Min && amount <= Max;
            }
            """, "SharedDomainLib");

        const string validConsumer = """
            using Verifiable.Contracts;
            using SharedDomain;

            namespace Consumer;

            [Verifiable]
            public static class Rules
            {
                public static decimal NonNegativeSpan(MoneyRange range, PositiveQty qty)
                {
                    Spec.Requires(range.Min > -1000000m && range.Max < 1000000m, "Bounded, so the span cannot overflow decimal.");
                    Spec.Ensures<decimal>(r => r >= 0m && qty >= 1);
                    return range.Span;
                }

                public static bool MidpointInside(MoneyRange range, decimal amount)
                {
                    Spec.Requires(range.Contains(amount));
                    Spec.Ensures<bool>(r => r);
                    return amount >= range.Min && amount <= range.Max;
                }

                public static MoneyRange Widen(MoneyRange range)
                {
                    Spec.Requires(range.Min > -1000000m && range.Max < 1000000m, "Bounded, so widening cannot overflow decimal.");
                    return new MoneyRange(range.Min - 1m, range.Max + 1m);
                }
            }
            """;

        var okDiags = await AnalyzerTestHarness.AnalyzeWithReferencesAsync(
            validConsumer,
            [libRef],
            null,
            null,
            new VerifiableGatekeeperAnalyzer(),
            new VerifiableSmtAnalyzer());
        Assert.Empty(okDiags.Where(d => d.Id == "AD0001" || d.Id == "VER104" || d.Severity == DiagnosticSeverity.Error));

        // Returning an inverted MoneyRange must fail the cross-assembly [Invariant] with VER102.
        const string brokenInvariantConsumer = """
            using Verifiable.Contracts;
            using SharedDomain;

            namespace Consumer;

            [Verifiable]
            public static class BadRules
            {
                public static MoneyRange Invert(MoneyRange range) =>
                    new MoneyRange(range.Max + 1m, range.Min);
            }
            """;

        var badDiags = await AnalyzerTestHarness.AnalyzeWithReferencesAsync(
            brokenInvariantConsumer,
            [libRef],
            null,
            null,
            new VerifiableGatekeeperAnalyzer(),
            new VerifiableSmtAnalyzer());
        Assert.DoesNotContain(badDiags, d => d.Id == "AD0001" || d.Id == "VER101" || d.Id == "VER104");
        Assert.Contains(badDiags, d => d.Id == "VER102" && d.GetMessage().Contains("Range minimum cannot exceed maximum."));
    }

    [Fact]
    public async Task CrossAssembly_GhostPredicateLinqExtensionAndPrivateHelperFailClosed()
    {
        var libRef = AnalyzerTestHarness.CompileLibraryReference("""
            using System.Collections.Immutable;
            using System.Linq;
            using Verifiable.Contracts;

            namespace SharedSpecs;

            public static class SpecLib
            {
                [Ghost]
                public static bool AllPositive(ImmutableList<int> items) => items.All(x => x > 0);

                private static bool Secret(int x) => x == 42;

                [Verifiable]
                public static bool UsesPrivateHelper(int x) => Secret(x);
            }
            """, "SharedSpecsLib");

        // Consumer does NOT even import System.Linq; exported extension call uses global::System.Linq.Enumerable.All!
        const string ghostConsumer = """
            using System.Collections.Immutable;
            using Verifiable.Contracts;

            namespace Consumer;

            [Verifiable]
            public static class Check
            {
                public static int FirstOrOne(ImmutableList<int> items)
                {
                    Spec.Requires(SharedSpecs.SpecLib.AllPositive(items));
                    Spec.Ensures<int>(r => r > 0);
                    return items.Count > 0 ? items[0] : 1;
                }
            }
            """;

        var okDiags = await AnalyzerTestHarness.AnalyzeWithReferencesAsync(
            ghostConsumer,
            [libRef],
            null,
            null,
            new VerifiableGatekeeperAnalyzer(),
            new VerifiableSmtAnalyzer());
        Assert.Empty(okDiags.Where(d => d.Id == "AD0001" || d.Id == "VER104" || d.Severity == DiagnosticSeverity.Error));

        // Private helper body is NOT exported (fail-closed): calling it inside Spec.Ensures yields VER101 cleanly, never AD0001.
        const string privateHelperConsumer = """
            using Verifiable.Contracts;

            namespace Consumer;

            [Verifiable]
            public static class CheckPrivate
            {
                public static bool Probe(int x)
                {
                    Spec.Ensures<bool>(r => r == SharedSpecs.SpecLib.UsesPrivateHelper(x));
                    return SharedSpecs.SpecLib.UsesPrivateHelper(x);
                }
            }
            """;

        var failClosedDiags = await AnalyzerTestHarness.AnalyzeWithReferencesAsync(
            privateHelperConsumer,
            [libRef],
            null,
            null,
            new VerifiableGatekeeperAnalyzer(),
            new VerifiableSmtAnalyzer());
        Assert.DoesNotContain(failClosedDiags, d => d.Id == "AD0001" || d.Id == "VER104");
        Assert.Contains(failClosedDiags, d => d.Id == "VER101");
    }
}
