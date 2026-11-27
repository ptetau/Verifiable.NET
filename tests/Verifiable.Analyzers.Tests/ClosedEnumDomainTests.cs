using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Verifiable.Analyzers.Smt;
using Xunit;

namespace Verifiable.Analyzers.Tests;

public class ClosedEnumDomainTests
{
    [Theory]
    [InlineData(null, 2)]
    [InlineData("false", 2)]
    [InlineData("true", 0)]
    public async Task ParametersAndNestedStoredFieldsAreClosedOnlyByOptIn(string? option, int failures)
    {
        await Check("""
            public enum State : long { Low = long.MinValue, Alias = Low, High = 7 }
            public enum Wide : ulong { Max = ulong.MaxValue }
            public readonly record struct Inner(Wide Value);
            public readonly struct Outer { public readonly Inner Field; }
            [Verifiable]
            public static class C
            {
                public static bool Direct(State state)
                {
                    Spec.Ensures<bool>(r => r);
                    return state == State.Low || state == State.High;
                }
                public static bool Nested(Outer outer)
                {
                    Spec.Ensures<bool>(r => r);
                    return outer.Field.Value == Wide.Max;
                }
            }
            """, option, failures);
    }

    [Fact]
    public async Task AddingAMemberBreaksAPreviouslyExhaustivePredicate()
    {
        const string original = """
            public enum Signal { Stop, Go }
            [Verifiable]
            public static class C
            {
                public static bool Known(Signal s)
                {
                    Spec.Ensures<bool>(r => r);
                    return s == Signal.Stop || s == Signal.Go;
                }
            }
            """;
        await Check(original, "true", 0);
        await Check(original.Replace("Stop, Go", "Stop, Go, Caution"), "true", 1);
    }

    [Fact]
    public async Task NarrowingCastUsesTheUnderlyingWidthWithoutAssumingMembership()
    {
        await Check("""
            public enum ByteState : byte { On = 1 }
            [Verifiable]
            public static class C
            {
                public static ByteState Wrapped(int n)
                {
                    Spec.Requires(n == 257);
                    Spec.Ensures<ByteState>(r => r == ByteState.On);
                    return (ByteState)n;
                }
                public static ByteState Unnamed(int n)
                {
                    Spec.Requires(n == 258);
                    Spec.Ensures<ByteState>(r => r == ByteState.On);
                    return (ByteState)n;
                }
            }
            """, "true", 1);
    }

    [Fact]
    public async Task FlagsCastsDefaultsComputedGettersAndCallResultsRemainOpen()
    {
        await Check("""
            [System.Flags] public enum Access { Read = 1, Write = 2 }
            public enum State { On = 1 }
            public readonly record struct Data(int Number) { public State Computed => (State)Number; }
            [Verifiable]
            public static class C
            {
                public static bool Flags(Access a)
                {
                    Spec.Ensures<bool>(r => r);
                    return a == Access.Read || a == Access.Write;
                }
                public static State Cast(int n)
                {
                    Spec.Ensures<State>(r => r == State.On);
                    return (State)n;
                }
                public static State Default()
                {
                    Spec.Ensures<State>(r => r == State.On);
                    return default;
                }
                public static State Computed(Data d)
                {
                    Spec.Ensures<State>(r => r == State.On);
                    return d.Computed;
                }
                public static State Identity(State s)
                {
                    Spec.Ensures<State>(r => r == State.On);
                    return s;
                }
                public static State ViaCall()
                {
                    Spec.Ensures<State>(r => r == State.On);
                    return Identity((State)2);
                }
            }
            """, "true", 5);
    }

    private static async Task Check(string source, string? option, int failures)
    {
        var options = option is null ? null : new Dictionary<string, string>
        {
            ["build_property.VerifiableClosedEnums"] = option
        };
        var diagnostics = await AnalyzerTestHarness.AnalyzeWithOptionsAsync(
            "using Verifiable.Contracts;\n" + source, options, new VerifiableSmtAnalyzer());
        Assert.DoesNotContain(diagnostics, d => d.Id == "AD0001" ||
            d.Severity == DiagnosticSeverity.Error && d.Id != "VER102" ||
            d.Id.StartsWith("VER") && d.Id != "VER102");
        Assert.Equal(failures, diagnostics.Count(d => d.Id == "VER102"));
    }
}
