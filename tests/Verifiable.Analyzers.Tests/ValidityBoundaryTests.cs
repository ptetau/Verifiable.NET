using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Verifiable.Analyzers.Smt;
using Xunit;

namespace Verifiable.Analyzers.Tests;

/// <summary>
/// Validity rules (TypeAlias <c>IsValid</c>, <c>[Invariant]</c>, closed enums) are assumed where a value enters a
/// method, so they must be proved where a value flows into such a boundary: a call argument, a returned
/// <c>T?</c> or array, a constructor's <c>Spec.Requires</c>.
/// </summary>
public class ValidityBoundaryTests
{
    private const string TodoId = """
        [TypeAlias<int>("Todo IDs start at 1.")]
        public readonly partial record struct TodoId
        {
            public static bool IsValid(int value) => value >= 1;
        }
        """;

    private static async Task<Diagnostic[]> RunAsync(string source)
    {
        var diags = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableGatekeeperAnalyzer(), new VerifiableSmtAnalyzer());
        return diags.ToArray();
    }

    private static bool Rejected(Diagnostic[] d) => d.Any(x => x.Severity == DiagnosticSeverity.Error || x.Id == "VER106");

    private static bool RejectedWith(Diagnostic[] d, string messagePart) =>
        d.Any(x => x.Severity == DiagnosticSeverity.Error && x.GetMessage().Contains(messagePart));

    private static string Wrap(string body) => $$"""
        using System.Collections.Immutable;
        using Verifiable.Contracts;

        namespace Sample;

        {{TodoId}}

        {{body}}
        """;

    private static string TypeAliasCaller(string requires) => Wrap($$"""
        [Verifiable]
        public static class C
        {
            public static int Raw(TodoId id)
            {
                Spec.Ensures<int>(r => r >= 1, "A todo id is at least 1.");
                return id.Value;
            }

            public static int Caller(int x)
            {
                {{requires}}
                Spec.Ensures<int>(r => r >= 1, "Still at least 1.");
                return Raw(new TodoId(x));
            }
        }
        """);

    [Fact]
    public async Task TypeAliasConstructedFromUnchecked_PassedToCallee_IsRejected()
    {
        var d = await RunAsync(TypeAliasCaller(""));
        Assert.True(RejectedWith(d, "TodoId.IsValid (argument 'id' of 'C.Raw')"), string.Join("\n", d.Select(x => x.ToString())));
    }

    [Fact]
    public async Task TypeAliasConstructedFromChecked_PassedToCallee_IsProved()
    {
        var d = await RunAsync(TypeAliasCaller("Spec.Requires(x >= 1, \"Positive.\");"));
        Assert.False(Rejected(d), string.Join("\n", d.Select(x => x.ToString())));
    }

    [Fact]
    public async Task TypeAliasParameter_PassedStraightThrough_IsProved()
    {
        var d = await RunAsync(Wrap("""
            [Verifiable]
            public static class C
            {
                public static int Raw(TodoId id)
                {
                    Spec.Ensures<int>(r => r >= 1, "A todo id is at least 1.");
                    return id.Value;
                }

                public static int Caller(TodoId id)
                {
                    Spec.Ensures<int>(r => r >= 1, "Still at least 1.");
                    return Raw(id);
                }
            }
            """));
        Assert.False(Rejected(d), string.Join("\n", d.Select(x => x.ToString())));
    }

    private static string InvariantCaller(string requires) => Wrap($$"""
        [Verifiable]
        public sealed record Span(int Lo, int Hi)
        {
            [Invariant("Lo never exceeds Hi.")]
            public bool Ordered() => Lo <= Hi;
        }

        [Verifiable]
        public static class C
        {
            public static int Width(Span s)
            {
                Spec.Ensures<int>(r => r >= 0, "A width is never negative.");
                return s.Hi - s.Lo;
            }

            public static int Caller(int a, int b)
            {
                {{requires}}
                Spec.Ensures<int>(r => r >= 0, "Still non-negative.");
                return Width(new Span(a, b));
            }
        }
        """);

    [Fact]
    public async Task InvariantRecordConstructedFromUnchecked_PassedToCallee_IsRejected()
    {
        var d = await RunAsync(InvariantCaller(""));
        Assert.True(RejectedWith(d, "[Invariant] Span.Ordered (argument 's' of 'C.Width')"), string.Join("\n", d.Select(x => x.ToString())));
    }

    [Fact]
    public async Task InvariantRecordConstructedFromChecked_PassedToCallee_IsProved()
    {
        var d = await RunAsync(InvariantCaller("Spec.Requires(a >= 0 && a <= b && b <= 1000, \"Ordered and small.\");"));
        Assert.False(Rejected(d), string.Join("\n", d.Select(x => x.ToString())));
    }

    private static string ClosedEnumRecordCaller(string requires) => Wrap($$"""
        [Verifiable]
        public enum Shade { Dark, Light }

        public readonly record struct LightState(Shade Shade);

        [Verifiable]
        public static class C
        {
            public static int Code(LightState s)
            {
                Spec.Ensures<int>(r => r == 0 || r == 1, "Codes are 0 or 1.");
                return (int)s.Shade;
            }

            public static int Caller(int raw)
            {
                {{requires}}
                Spec.Ensures<int>(r => r == 0 || r == 1, "Codes are 0 or 1.");
                return Code(new LightState((Shade)raw));
            }
        }
        """);

    [Fact]
    public async Task ClosedEnumInsideRecordArgument_FromUnchecked_IsRejected()
    {
        var d = await RunAsync(ClosedEnumRecordCaller("Spec.Requires(raw >= 0 && raw <= 10, \"Small.\");"));
        Assert.True(RejectedWith(d, "Closed enum domain for 'Shade' (argument 's' of 'C.Code')"), string.Join("\n", d.Select(x => x.ToString())));
    }

    [Fact]
    public async Task ClosedEnumInsideRecordArgument_FromChecked_IsProved()
    {
        var d = await RunAsync(ClosedEnumRecordCaller("Spec.Requires(raw == 0 || raw == 1, \"A declared member.\");"));
        Assert.False(Rejected(d), string.Join("\n", d.Select(x => x.ToString())));
    }

    private static string NullableReturn(string requires) => Wrap($$"""
        [Verifiable]
        public static class C
        {
            public static TodoId? Make(int x)
            {
                {{requires}}
                Spec.Ensures<TodoId?>(r => r.HasValue, "Always has a value.");
                TodoId? id = new TodoId(x);
                return id;
            }
        }
        """);

    [Fact]
    public async Task ReturningNullableTypeAlias_WithInvalidPayload_IsRejected()
    {
        var d = await RunAsync(NullableReturn(""));
        Assert.True(RejectedWith(d, "TodoId.IsValid"), string.Join("\n", d.Select(x => x.ToString())));
    }

    [Fact]
    public async Task ReturningNullableTypeAlias_WithValidPayload_IsProved()
    {
        var d = await RunAsync(NullableReturn("Spec.Requires(x >= 1, \"Positive.\");"));
        Assert.False(Rejected(d), string.Join("\n", d.Select(x => x.ToString())));
    }

    [Fact]
    public async Task ReturningAbsentNullableTypeAlias_IsProved()
    {
        var d = await RunAsync(Wrap("""
            [Verifiable]
            public static class C
            {
                public static TodoId? Make(int x)
                {
                    if (x < 1) return null;
                    return new TodoId(x);
                }
            }
            """));
        Assert.False(Rejected(d), string.Join("\n", d.Select(x => x.ToString())));
    }

    private static string ReturnedArray(string requires) => Wrap($$"""
        [Verifiable]
        public static class C
        {
            public static ImmutableArray<TodoId> Make(int x)
            {
                {{requires}}
                Spec.Ensures<ImmutableArray<TodoId>>(r => !r.IsDefault && r.Length == 1, "One initialized element.");
                return ImmutableArray.Create(new TodoId(x));
            }
        }
        """);

    [Fact]
    public async Task ReturningArrayOfTypeAlias_WithInvalidElement_IsRejected()
    {
        var d = await RunAsync(ReturnedArray(""));
        Assert.True(RejectedWith(d, "every element of the ImmutableArray is valid"), string.Join("\n", d.Select(x => x.ToString())));
    }

    [Fact]
    public async Task ReturningArrayOfTypeAlias_WithValidElement_IsProved()
    {
        var d = await RunAsync(ReturnedArray("Spec.Requires(x >= 1, \"Positive.\");"));
        Assert.False(Rejected(d), string.Join("\n", d.Select(x => x.ToString())));
    }

    [Fact]
    public async Task ReturningArrayParameter_AndAppendingValidElement_IsProved()
    {
        var d = await RunAsync(Wrap("""
            [Verifiable]
            public static class C
            {
                public static ImmutableArray<TodoId> Same(ImmutableArray<TodoId> ids)
                {
                    return ids;
                }

                public static ImmutableArray<TodoId> Append(ImmutableArray<TodoId> ids, TodoId extra)
                {
                    Spec.Requires(!ids.IsDefault, "Initialized.");
                    return ids.Add(extra);
                }
            }
            """));
        Assert.False(Rejected(d), string.Join("\n", d.Select(x => x.ToString())));
    }

    [Fact]
    public async Task ReturningArrayParameter_AppendingInvalidElement_IsRejected()
    {
        var d = await RunAsync(Wrap("""
            [Verifiable]
            public static class C
            {
                public static ImmutableArray<TodoId> Append(ImmutableArray<TodoId> ids, int x)
                {
                    Spec.Requires(!ids.IsDefault, "Initialized.");
                    return ids.Add(new TodoId(x));
                }
            }
            """));
        Assert.True(RejectedWith(d, "every element of the ImmutableArray is valid"), string.Join("\n", d.Select(x => x.ToString())));
    }

    [Fact]
    public async Task PassingArrayOfTypeAlias_ToCallee_ChecksElements()
    {
        const string callee = """
            [Verifiable]
            public static class C
            {
                public static int First(ImmutableArray<TodoId> ids)
                {
                    Spec.Requires(!ids.IsDefault && ids.Length >= 1, "Non-empty.");
                    Spec.Ensures<int>(r => r >= 1, "A todo id is at least 1.");
                    return ids[0].Value;
                }

                public static int Caller(int x)
                {
                    Spec.Requires(x >= -5 && x <= 5, "Small.");
                    Spec.Ensures<int>(r => r >= 1, "Still at least 1.");
                    return First(ImmutableArray.Create(new TodoId(x)));
                }
            }
            """;
        var bad = await RunAsync(Wrap(callee));
        Assert.True(RejectedWith(bad, "argument 'ids' of 'C.First'"), string.Join("\n", bad.Select(x => x.ToString())));

        var good = await RunAsync(Wrap(callee.Replace("x >= -5 && x <= 5", "x >= 1 && x <= 5")));
        Assert.False(Rejected(good), string.Join("\n", good.Select(x => x.ToString())));
    }

    [Fact]
    public async Task InvariantRecordsInNestedLists_PassedThroughAndAppended_AreProved()
    {
        var d = await RunAsync(Wrap("""
            [Verifiable]
            public readonly record struct Line(TodoId Id);

            [Verifiable]
            public readonly record struct Entry(TodoId Id, ImmutableList<Line> Lines, TodoId? Reverses)
            {
                [Invariant("Two lines.")]
                public bool Ok() => Lines.Count >= 2;
            }

            [Verifiable]
            public readonly record struct State(ImmutableList<Entry> Journal);

            [Verifiable]
            public static class Eng
            {
                public static State Same(State s) => s with { Journal = s.Journal };

                public static State Post(State s, Entry e) => s with { Journal = s.Journal.Add(e) };
            }
            """));
        Assert.False(Rejected(d), string.Join("\n", d.Select(x => x.ToString())));
    }

    private static string CtorRequires(string requires) => Wrap($$"""
        [Verifiable]
        public readonly record struct Age
        {
            public Age(int years)
            {
                Spec.Requires(years >= 0, "Ages are never negative.");
                Years = years;
            }

            public int Years { get; }
        }

        [Verifiable]
        public static class C
        {
            public static int Make(int x)
            {
                {{requires}}
                var a = new Age(x);
                return a.Years;
            }
        }
        """);

    [Fact]
    public async Task ConstructorRequires_NotMetByCaller_IsRejected()
    {
        var d = await RunAsync(CtorRequires("Spec.Requires(x >= -5 && x <= 5, \"Small inputs only.\");"));
        Assert.True(RejectedWith(d, "Ages are never negative"), string.Join("\n", d.Select(x => x.ToString())));
    }

    [Fact]
    public async Task ConstructorRequires_MetByCaller_IsProved()
    {
        var d = await RunAsync(CtorRequires("Spec.Requires(x >= 0 && x <= 5, \"Small inputs only.\");"));
        Assert.False(Rejected(d), string.Join("\n", d.Select(x => x.ToString())));
    }

    private static string MapToArray(string sourceAttribute, string requires) => $$"""
        using System.Collections.Immutable;
        using Verifiable.Contracts;

        namespace Sample;

        [MapTo(typeof(ListState))]
        public class ListDto
        {
            {{sourceAttribute}}
            public ImmutableArray<int> Items { get; set; }
        }

        public readonly record struct ListState([property: NonDefault] ImmutableArray<int> Items);

        [Verifiable]
        public static class Mapper
        {
            public static int Handle(ListDto dto)
            {
                {{requires}}
                Spec.Ensures<int>(r => r >= 0, "A length is never negative.");
                var s = dto.ToListState();
                return s.Items.Length;
            }
        }
        """;

    [Fact]
    public async Task MapTo_PossiblyDefaultArrayIntoNonDefaultField_IsRejectedAtTheCall()
    {
        var d = await RunAsync(MapToArray("", ""));
        Assert.True(RejectedWith(d, "!source.Items.IsDefault"), string.Join("\n", d.Select(x => x.ToString())));
    }

    [Fact]
    public async Task MapTo_ArrayProvedInitializedByCaller_IsProved()
    {
        var d = await RunAsync(MapToArray("", "Spec.Requires(!dto.Items.IsDefault, \"Initialized.\");"));
        Assert.False(Rejected(d), string.Join("\n", d.Select(x => x.ToString())));
    }

    [Fact]
    public async Task MapTo_NonDefaultSourceMember_NeedsNoRequirement()
    {
        var d = await RunAsync(MapToArray("[NonDefault]", ""));
        Assert.False(Rejected(d), string.Join("\n", d.Select(x => x.ToString())));
    }
}
