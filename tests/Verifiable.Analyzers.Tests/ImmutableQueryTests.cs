using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Verifiable.Analyzers.Smt;
using Xunit;

namespace Verifiable.Analyzers.Tests;

public class ImmutableQueryTests
{
    private static async Task<Diagnostic[]> Analyze(string method)
    {
        var diagnostics = await AnalyzerTestHarness.AnalyzeWithReferencesAsync("""
            using System.Collections.Generic;
            using System.Collections.Immutable;
            using System.Linq;
            using Verifiable.Contracts;
            public readonly record struct LineItem(int CategoryId, decimal Amount);
            [Verifiable]
            public static class Rules {
            """ + "\n" + method + "\n}", null, [typeof(Enumerable), typeof(IQueryable<>), typeof(Queryable)],
            new VerifiableGatekeeperAnalyzer(), new VerifiableSmtAnalyzer());
        return diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error || d.Id == "AD0001").ToArray();
    }

    [Fact]
    public async Task DuplicateCategoryMatchesTwoDistinctIndices()
    {
        Assert.Empty(await Analyze("""
            public static bool Duplicate(ImmutableList<LineItem> items) {
                Spec.Requires(items.Count == 2);
                Spec.Ensures<bool>(r => r == (items[0].CategoryId == items[1].CategoryId));
                Spec.Ensures<bool>(r => r == items.GroupBy(i => i.CategoryId).Any(g => g.Count() > 1));
                return items.GroupBy(i => i.CategoryId).Any(g => g.Count() > 1);
            }
            """));
    }

    [Fact]
    public async Task ArrayPipelineBindsFiltersProjectionsAndCaptures()
    {
        Assert.Empty(await Analyze("""
            public static bool High(ImmutableArray<LineItem> items, int minimum) {
                Spec.Requires(!items.IsDefault);
                Spec.Ensures<bool>(r => r == !items.All(i => i.CategoryId <= minimum));
                return items.Select(i => i.CategoryId).Where(c => c > minimum).Any();
            }
            """));
    }

    [Fact]
    public async Task CountCountsEveryMatchingIndex()
    {
        Assert.Empty(await Analyze("""
            public static int Positives(ImmutableList<int> xs) {
                Spec.Requires(xs.Count == 2 && xs[0] > 0 && xs[1] > 0);
                Spec.Ensures<int>(r => r == 2);
                return xs.Count(x => x > 0);
            }

            """));
    }

    [Fact]
    public async Task FilteredCountRespectsBoundsAndExistence()
    {
        Assert.Empty(await Analyze("""
            public static int Positives(ImmutableList<int> xs) {
                Spec.Ensures<int>(r => r >= 0 && r <= xs.Count && (r > 0) == xs.Any(x => x > 0));
                return xs.Where(x => x > 0).Count();
            }
            """));
    }

    [Fact]
    public async Task PredicatesUseEnumMembersAndScalarConstants()
    {
        Assert.Empty(await Analyze("""
            public enum State { Active = 7 }
            public const int Minimum = 3;
            public static bool Active(ImmutableList<State> xs) {
                Spec.Requires(xs.Count == 1 && xs[0] == State.Active);
                Spec.Ensures<bool>(r => r);
                return xs.Any(x => x == State.Active);
            }
            public static int CountAboveMinimum(ImmutableList<int> xs) {
                Spec.Requires(xs.Count == 1 && xs[0] == 3);
                Spec.Ensures<int>(r => r == 0);
                return xs.Where(x => x > Minimum).Count();
            }
            """));
    }

    [Fact]
    public async Task EmptySequenceRefutesExistential()
    {
        var errors = await Analyze("""
            public static bool Nonempty(ImmutableList<int> xs) {
                Spec.Requires(xs.Count == 0);
                Spec.Ensures<bool>(r => r);
                return xs.Any(x => x == x);
            }
            """);
        Assert.Equal("VER102", Assert.Single(errors).Id);
    }

    [Theory]
    [InlineData("ImmutableList<int> xs", "xs.Where((x, i) => x == i).Any()")]
    [InlineData("IReadOnlyList<int> xs", "xs.Any()")]
    [InlineData("ImmutableList<int> xs", "xs.GroupBy(x => x).Any(g => g.Count() > 2)")]
    [InlineData("ImmutableList<int> xs, int n", "xs.All(x => x > n++)")]
    public async Task UnsupportedQueriesAreDiagnosedEvenWithoutPostcondition(string parameters, string expression)
    {
        var errors = await Analyze($"public static bool F({parameters}) => {expression};");
        Assert.Contains(errors, d => d.Id == "VER101");
        Assert.DoesNotContain(errors, d => d.Id.StartsWith("CS") || d.Id == "AD0001");
    }

    [Fact]
    public async Task QueryableProviderIsNotTrustedAsEnumerable()
    {
        var errors = await Analyze("public static bool Any(IQueryable<int> xs) => xs.Any();");
        Assert.Contains(errors, d => d.Id == "VER006");
        Assert.DoesNotContain(errors, d => d.Id.StartsWith("CS") || d.Id == "AD0001");
    }

    [Theory]
    [InlineData("return default(ImmutableArray<int>).Any();")]
    [InlineData("var xs = default(ImmutableArray<int>); return xs.Any();")]
    [InlineData("return input.Any();")]
    public async Task DefaultImmutableArrayCannotBeEnumerated(string body)
    {
        var errors = await Analyze($"public static bool F(ImmutableArray<int> input) {{ {body} }}");
        Assert.Contains(errors, d => d.Id == "VER102" && d.GetMessage().Contains("must not be default"));
        Assert.DoesNotContain(errors, d => d.Id.StartsWith("CS") || d.Id == "AD0001");
    }

    [Fact]
    public async Task InitializedEmptyIsDistinctFromDefault()
    {
        Assert.Empty(await Analyze("""
            public static bool Empty() {
                Spec.Ensures<bool>(r => !r);
                return ImmutableArray<int>.Empty.Any();
            }
            """));
    }

    [Fact]
    public async Task LongCountRemainsAvailableInOrdinaryImplementation()
    {
        Assert.Empty(await Analyze("public static long Size(ImmutableList<int> xs) => xs.LongCount();"));
    }

    [Theory]
    [InlineData(32, false)]
    [InlineData(33, true)]
    public async Task PipelineExpansionIsBounded(int stages, bool exceedsLimit)
    {
        var query = "xs" + string.Concat(Enumerable.Repeat(".Select(x => x == x)", stages)) + ".Any(x => x)";
        var errors = await Analyze($"public static bool F(ImmutableList<bool> xs) {{ Spec.Ensures<bool>(r => r == (xs.Count > 0)); return {query}; }}");
        if (exceedsLimit)
            Assert.Contains(errors, d => d.Id == "VER101" && d.GetMessage().Contains("pipeline limit"));
        else
            Assert.Empty(errors);
    }
}
