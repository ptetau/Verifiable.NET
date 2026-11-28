using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Verifiable.Analyzers.Smt;
using Xunit;

namespace Verifiable.Analyzers.Tests;

/// <summary>
/// Equality and object construction run user code in C# (a declared Equals or operator ==, a constructor body, a
/// property initializer, a computed property). The model follows that code or refuses with VER101; it never replaces
/// it with field-by-field equality or a by-name binding of constructor arguments.
/// </summary>
public class EqualityModelTests
{
    private static async Task<Diagnostic[]> Analyze(string source) =>
        (await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableGatekeeperAnalyzer(), new VerifiableSmtAnalyzer())).ToArray();

    private static async Task AssertClean(string source)
    {
        var diags = await Analyze(source);
        Assert.True(diags.All(d => d.Id != "AD0001" && d.Id != "VER104" && d.Severity != DiagnosticSeverity.Error),
            string.Join("\n", diags.Select(d => $"{d.Id}: {d.GetMessage()}")));
    }

    private static async Task AssertRejected(string source, string id, string messagePart)
    {
        var diags = await Analyze(source);
        Assert.DoesNotContain(diags, d => d.Id == "AD0001");
        Assert.True(diags.Any(d => d.Id == id && d.GetMessage().Contains(messagePart)),
            $"expected {id} containing '{messagePart}', got:\n" + string.Join("\n", diags.Select(d => $"{d.Id}: {d.GetMessage()}")));
    }

    [Fact]
    public async Task RecordWithOwnEquals_NotVerifiable_IsRejected()
    {
        await AssertRejected("""
            using Verifiable.Contracts;
            namespace Sample;

            public sealed record Pair(int A, int B)
            {
                public bool Equals(Pair? other) => other is not null && A == other.A;
                public override int GetHashCode() => A;
            }

            [Verifiable]
            public static class Eq
            {
                public static bool Differ(int a)
                {
                    Spec.Ensures<bool>(r => r == false);
                    return new Pair(a, 1) == new Pair(a, 2);
                }
            }
            """, "VER101", "declares its own Equals");
    }

    [Fact]
    public async Task RecordStructWithOwnEquals_IsModeledAsACallToThatEquals()
    {
        const string template = """
            using Verifiable.Contracts;
            namespace Sample;

            [Verifiable]
            public readonly record struct Loose(int V)
            {
                public bool Equals(Loose other) => BODY;
                public override int GetHashCode() => 0;
            }

            [Verifiable]
            public static class Eq
            {
                public static bool Same(int a, int b)
                {
                    Spec.Ensures<bool>(r => r == (a == b));
                    return new Loose(a) == new Loose(b);
                }

                public static bool Different(int a, int b)
                {
                    Spec.Ensures<bool>(r => r == (a != b));
                    return new Loose(a) != new Loose(b);
                }
            }
            """;

        await AssertClean(template.Replace("BODY", "V == other.V"));
        var diags = await Analyze(template.Replace("BODY", "true"));
        Assert.Contains(diags, d => d.Id == "VER102");
    }

    [Fact]
    public async Task UserDefinedEqualityOperator_BodyIsFollowed()
    {
        const string template = """
            using Verifiable.Contracts;
            namespace Sample;

            [Verifiable]
            public readonly struct Token
            {
                public Token(int id) { Id = id; }
                public int Id { get; }
                public static bool operator ==(Token a, Token b) => BODY;
                public static bool operator !=(Token a, Token b) => !(a == b);
                public override bool Equals(object? obj) => obj is Token t && t == this;
                public override int GetHashCode() => Id;
            }

            [Verifiable]
            public static class Eq
            {
                public static bool Same(int id)
                {
                    Spec.Ensures<bool>(r => r);
                    var t = new Token(id);
                    return t == t;
                }
            }
            """;

        await AssertClean(template.Replace("BODY", "a.Id == b.Id"));
        var diags = await Analyze(template.Replace("BODY", "false"));
        Assert.Contains(diags, d => d.Id == "VER102");
    }

    [Fact]
    public async Task UserDefinedEqualityOperator_OutsideVerifiableScope_IsRejected()
    {
        await AssertRejected("""
            using Verifiable.Contracts;
            namespace Sample;

            public readonly struct Token
            {
                public Token(int id) { Id = id; }
                public int Id { get; }
                public static bool operator ==(Token a, Token b) => false;
                public static bool operator !=(Token a, Token b) => true;
                public override bool Equals(object? obj) => false;
                public override int GetHashCode() => Id;
            }

            [Verifiable]
            public static class Eq
            {
                public static bool Same(int id)
                {
                    var t = new Token(id);
                    return t == t;
                }
            }
            """, "VER101", "not [Verifiable]");
    }

    [Fact]
    public async Task ImmutableArrayEquality_IsRejected_ButDefaultCheckIsModeled()
    {
        await AssertRejected("""
            using System.Collections.Immutable;
            using Verifiable.Contracts;
            namespace Sample;

            [Verifiable]
            public static class Arrays
            {
                public static bool Same(int x)
                {
                    return ImmutableArray.Create(x) == ImmutableArray.Create(x);
                }
            }
            """, "VER101", "SequenceEqual");

        await AssertRejected("""
            using System.Collections.Immutable;
            using Verifiable.Contracts;
            namespace Sample;

            public sealed record Holder(ImmutableArray<int> Items);

            [Verifiable]
            public static class Arrays
            {
                public static bool Same(Holder a, Holder b)
                {
                    return a == b;
                }
            }
            """, "VER101", "SequenceEqual");

        await AssertClean("""
            using System.Collections.Immutable;
            using Verifiable.Contracts;
            namespace Sample;

            [Verifiable]
            public static class Arrays
            {
                public static bool IsUnset(ImmutableArray<int> items)
                {
                    Spec.Ensures<bool>(r => r == items.IsDefault);
                    return items == default;
                }

                public static bool IsSet(ImmutableArray<int> items)
                {
                    Spec.Ensures<bool>(r => r == !items.IsDefault);
                    return items != default;
                }
            }
            """);
    }

    [Fact]
    public async Task DateTimeOffsetInsideRecordOrTuple_IsComparedByInstant()
    {
        await AssertClean("""
            using System;
            using Verifiable.Contracts;
            namespace Sample;

            public readonly record struct Stamp(DateTimeOffset At);

            [Verifiable]
            public static class Stamps
            {
                public static bool SameInstant(DateTimeOffset a, DateTimeOffset b)
                {
                    Spec.Requires(a == b);
                    Spec.Ensures<bool>(r => r);
                    return new Stamp(a) == new Stamp(b);
                }

                public static bool SameInstantInTuple(DateTimeOffset a, DateTimeOffset b, int n)
                {
                    Spec.Requires(a == b);
                    Spec.Ensures<bool>(r => r);
                    return (a, n) == (b, n);
                }

                public static bool OtherInstant(DateTimeOffset a, DateTimeOffset b)
                {
                    Spec.Requires(a != b);
                    Spec.Ensures<bool>(r => !r);
                    return new Stamp(a) == new Stamp(b);
                }
            }
            """);
    }

    [Fact]
    public async Task TupleEquality_IsElementWise()
    {
        await AssertClean("""
            using Verifiable.Contracts;
            namespace Sample;

            [Verifiable]
            public static class Tuples
            {
                public static bool Same(int a, int b)
                {
                    Spec.Ensures<bool>(r => r == (a == b));
                    return (a, b) == (b, a);
                }

                public static bool Differs(int a, int b, string s)
                {
                    Spec.Ensures<bool>(r => r == (a != b || s != "x"));
                    return (a, s) != (b, "x");
                }

                public static bool OfVariables(int a, int b, int c, int d)
                {
                    Spec.Ensures<bool>(r => r == (a == c && b == d));
                    var left = (a, b);
                    var right = (c, d);
                    return left == right;
                }
            }
            """);

        var wrong = await Analyze("""
            using Verifiable.Contracts;
            namespace Sample;

            [Verifiable]
            public static class Tuples
            {
                public static bool OnlyFirst(int a, int b, int c, int d)
                {
                    Spec.Ensures<bool>(r => r == (a == c));
                    return (a, b) == (c, d);
                }
            }
            """);
        Assert.Contains(wrong, d => d.Id == "VER102");
    }

    [Fact]
    public async Task TupleEquality_WithUserEqualityElement_IsRejected()
    {
        await AssertRejected("""
            using Verifiable.Contracts;
            namespace Sample;

            public readonly struct Token
            {
                public Token(int id) { Id = id; }
                public int Id { get; }
                public static bool operator ==(Token a, Token b) => false;
                public static bool operator !=(Token a, Token b) => true;
                public override bool Equals(object? obj) => false;
                public override int GetHashCode() => Id;
            }

            [Verifiable]
            public static class Tuples
            {
                public static bool Same(int id)
                {
                    var t = new Token(id);
                    return (t, 1) == (t, 1);
                }
            }
            """, "VER101", "cannot be compared");
    }

    [Fact]
    public async Task RecordPropertyThatShadowsAParameter_IsRejected()
    {
        await AssertRejected("""
            using Verifiable.Contracts;
            namespace Sample;

            public sealed record Doubled(int X)
            {
                public int X { get; } = X * 2;
            }

            [Verifiable]
            public static class Shadow
            {
                public static int Get(int n)
                {
                    Spec.Requires(n >= 1 && n <= 1000);
                    Spec.Ensures<int>(r => r == n);
                    return new Doubled(n).X;
                }
            }
            """, "VER101", "plain 'field = expression' assignments");
    }

    [Fact]
    public async Task ExplicitConstructor_StraightLineAssignmentsAreFollowed()
    {
        const string template = """
            using Verifiable.Contracts;
            namespace Sample;

            public readonly struct Clamped
            {
                public readonly int N;
                public Clamped(int n) { N = n < 0 ? 0 : n; }
            }

            public readonly record struct Pair
            {
                public Pair(int a, int b) { A = b; B = a; }
                public int A { get; }
                public int B { get; }
            }

            [Verifiable]
            public static class Ctors
            {
                public static int Clamp(int n)
                {
                    Spec.Ensures<int>(r => r == EXPECTED);
                    return new Clamped(n).N;
                }

                public static int First(int a, int b)
                {
                    Spec.Ensures<int>(r => r == b);
                    return new Pair(a, b).A;
                }
            }
            """;

        await AssertClean(template.Replace("EXPECTED", "(n < 0 ? 0 : n)"));
        var diags = await Analyze(template.Replace("EXPECTED", "n"));
        Assert.Contains(diags, d => d.Id == "VER102");
    }

    [Fact]
    public async Task ExplicitConstructor_WithBranchOrChaining_IsRejected()
    {
        await AssertRejected("""
            using System;
            using Verifiable.Contracts;
            namespace Sample;

            public readonly struct Positive
            {
                public readonly int N;
                public Positive(int n)
                {
                    if (n < 0) throw new ArgumentOutOfRangeException(nameof(n));
                    N = n;
                }
            }

            [Verifiable]
            public static class Ctors
            {
                public static int Get(int n)
                {
                    Spec.Requires(n >= 0);
                    return new Positive(n).N;
                }
            }
            """, "VER101", "plain 'field = expression' assignments");
    }

    [Fact]
    public async Task ExplicitConstructor_RequiresIsCheckedAtTheCall()
    {
        const string template = """
            using Verifiable.Contracts;
            namespace Sample;

            [Verifiable]
            public readonly record struct Age
            {
                public Age(int years)
                {
                    Spec.Requires(years >= 0);
                    Years = years;
                }
                public int Years { get; }
            }

            [Verifiable]
            public static class Ctors
            {
                public static int Make(int x)
                {
                    REQUIRES
                    return new Age(x).Years;
                }
            }
            """;

        await AssertClean(template.Replace("REQUIRES", "Spec.Requires(x >= 0);"));
        var diags = await Analyze(template.Replace("REQUIRES", ""));
        Assert.Contains(diags, d => d.Id == "VER102");
    }

    [Fact]
    public async Task ComputedPropertyDivision_IsCheckedAtTheAccess()
    {
        const string template = """
            using Verifiable.Contracts;
            namespace Sample;

            public readonly record struct Ratio(int A, int B)
            {
                public int Quotient => A / B;
            }

            [Verifiable]
            public static class Ratios
            {
                public static int Get(Ratio r)
                {
                    GUARD
                    return r.Quotient;
                }
            }
            """;

        var unguarded = await Analyze(template.Replace("GUARD", "Spec.Ensures<int>(x => true);"));
        Assert.Contains(unguarded, d => d.Id == "VER102" && d.GetMessage().Contains("ivi"));
        await AssertClean(template.Replace("GUARD", "Spec.Requires(r.B > 0);"));

        await AssertClean("""
            using Verifiable.Contracts;
            namespace Sample;

            public readonly record struct Ratio(int A, int B)
            {
                public int Quotient => A / B;
            }

            [Verifiable]
            public static class Ratios
            {
                public static int Safe(Ratio r) => r.B > 0 ? r.Quotient : 0;
            }
            """);
    }
}
