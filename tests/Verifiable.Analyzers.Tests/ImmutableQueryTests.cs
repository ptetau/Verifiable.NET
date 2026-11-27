using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Verifiable.Analyzers.Smt;
using Xunit;

namespace Verifiable.Analyzers.Tests;

public class ImmutableQueryTests
{
    [Fact]
    public async Task D17_GroupByDuplicateDetection_ProvesAndRefutes()
    {
        const string source = """
            using System.Collections.Immutable;
            using System.Linq;
            using Verifiable.Contracts;

            namespace Sample;

            public readonly record struct LineItem(int CategoryId, decimal Amount);

            [Verifiable]
            public static class InvoiceRules
            {
                public static bool HasDuplicateCategory(ImmutableList<LineItem> items)
                {
                    Spec.Ensures<bool>(r => r == items.GroupBy(i => i.CategoryId).Any(g => g.Count() > 1));
                    return items.GroupBy(i => i.CategoryId).Any(g => g.Count() > 1);
                }

                public static bool UniqueCategoriesEquivalence(ImmutableList<LineItem> items)
                {
                    Spec.Ensures<bool>(r => r);
                    return items.GroupBy(i => i.CategoryId).All(g => g.Count() == 1)
                        == !items.GroupBy(i => i.CategoryId).Any(g => g.Count() > 1);
                }
            }
            """;

        await CheckClean(source);
    }

    [Fact]
    public async Task LinqPipelines_Any_All_Count_Where_Select_WithEnumAndConstPredicates()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Collections.Immutable;
            using System.Linq;
            using Verifiable.Contracts;

            namespace Sample;

            public enum State { Idle = 0, Active = 1, Done = 2 }

            public static class Limits
            {
                public const int Minimum = 10;
            }

            public readonly record struct TaskItem(State Status, int Score);

            [Verifiable]
            public static class QueryRules
            {
                public static bool HasActive(ImmutableList<TaskItem> items)
                {
                    Spec.Ensures<bool>(r => r == items.Select(x => x.Status).Any(s => s == State.Active));
                    return items.Any(x => x.Status == State.Active);
                }

                public static int CountAboveMin(IReadOnlyList<int> xs)
                {
                    Spec.Ensures<int>(r => r == xs.Count(x => x > Limits.Minimum) && r >= 0 && r <= xs.Count);
                    return xs.Where(x => x > Limits.Minimum).Count();
                }

                public static bool CountPositiveMatchesAny(ImmutableList<int> xs)
                {
                    Spec.Ensures<bool>(r => r);
                    return (xs.Count(x => x > 0) > 0) == xs.Any(x => x > 0);
                }

                public static bool ConcreteLiteralCount()
                {
                    ImmutableList<int> xs = [-1, 5, 10];
                    Spec.Ensures<bool>(r => r);
                    return xs.Count(x => x > 0) == 2 && xs.Any(x => x == 5) && !xs.All(x => x > 0);
                }
            }
            """;

        await CheckClean(source);
    }

    [Fact]
    public async Task DefaultImmutableArray_IsCaughtOnDirectQueryAndProducerFollowedByQuery()
    {
        const string directDefaultSource = """
            using System.Collections.Immutable;
            using System.Linq;
            using Verifiable.Contracts;

            namespace Sample;

            [Verifiable]
            public static class ArrayQueries
            {
                public static bool UnsafeAny(ImmutableArray<int> input)
                {
                    Spec.Ensures<bool>(r => r == input.Any());
                    return input.Any();
                }
            }
            """;

        await CheckExpecting(directDefaultSource, "VER102");

        // Regression: default array passed to .Add(1).Any() throws in Add before enumeration!
        const string producerDefaultSource = """
            using System.Collections.Immutable;
            using System.Linq;
            using Verifiable.Contracts;

            namespace Sample;

            [Verifiable]
            public static class ProducerQueries
            {
                public static bool AddThenAnyOnDefault(ImmutableArray<int> input)
                {
                    Spec.Requires(input.IsDefault);
                    Spec.Ensures<bool>(r => r);
                    return input.Add(1).Any();
                }
            }
            """;

        await CheckExpecting(producerDefaultSource, "VER102");
    }

    [Fact]
    public async Task GuardedNonDefaultImmutableArray_VerifiesCleanly()
    {
        const string source = """
            using System.Collections.Immutable;
            using System.Linq;
            using Verifiable.Contracts;

            namespace Sample;

            [Verifiable]
            public static class SafeArrayQueries
            {
                public static bool SafeAnyPositive(ImmutableArray<int> input)
                {
                    Spec.Ensures<bool>(r => !input.IsDefaultOrEmpty || !r);
                    if (input.IsDefault) return false;
                    return input.Any(x => x > 0);
                }

                public static bool EmptyAddIsNonEmpty()
                {
                    Spec.Ensures<bool>(r => r);
                    return ImmutableArray<int>.Empty.Add(42).Any(x => x == 42);
                }
            }
            """;

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
