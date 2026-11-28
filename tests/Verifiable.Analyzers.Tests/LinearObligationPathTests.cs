using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Verifiable.Analyzers.Tests;

/// <summary>
/// <c>VER007</c> on the paths and shapes it used to miss (issue #34): catch and finally blocks, a second
/// consumption after a partial one, tokens handed to methods that are not sinks, tokens put in tuples, arrays and
/// nullables, out parameters, captured tokens, and the correct programs it used to reject.
/// </summary>
public class LinearObligationPathTests
{
    private const string Preamble = """
        using System;
        using System.Collections.Generic;
        using System.Threading.Tasks;
        using Verifiable.Contracts;

        namespace PathDomain;

        [MustConsume]
        public readonly record struct Token(int Id);

        public static class Flow
        {
            public static Token Make(int id) => new(id);
            public static (Token, int) MakePair() => (new Token(1), 2);
            public static void Work() { }
            public static bool Check() => true;

            [ConsumesObligation]
            public static void Sink(Token t) { _ = t.Id; }

            [ConsumesObligation]
            public static async Task SinkAsync(Token t) { await Task.Yield(); _ = t.Id; }

            public static void Make2(out Token made) { made = Make(1); }

        """;

    private const string Postamble = "\n    }\n";

    private static async Task<string[]> Run(string members)
    {
        var diags = await AnalyzerTestHarness.AnalyzeAsync(Preamble + members + Postamble, new LinearObligationAnalyzer());
        Assert.DoesNotContain(diags, d => d.Id == "AD0001");
        return diags
            .Where(d => d.Id == "VER007")
            .Select(d => d.GetMessage().Split('\n')[0].TrimEnd('\r'))
            .ToArray();
    }

    public static TheoryData<string> CorrectPrograms => new()
    {
        // Reassigning a variable after its token was consumed (was reported as a dropped temporary).
        "static void P() { var t = Make(1); Sink(t); t = Make(2); Sink(t); }",
        // Tuples: a swap keeps both tokens live; a deconstruction hands one out.
        "static void P() { var a = Make(1); var b = Make(2); (a, b) = (b, a); Sink(a); Sink(b); }",
        "static int P() { var (a, b) = (Make(1), 1); Sink(a); return b; }",
        "static void P() { var (a, b) = MakePair(); Sink(a); Work(); _ = b; }",
        "static (Token, int) P() { var t = Make(1); return (t, 3); }",
        // A conditional in a later argument spills the token into a flow capture.
        "static void P(bool c) { var t = Make(1); Obligation.Discard(t, c ? \"x\" : \"y\"); }",
        // Both branches consume once each.
        "static void P(bool c) { var a = Make(1); var b = Make(2); if (c) { Sink(a); Sink(b); } else { Obligation.Discard(a, \"d\"); Obligation.Discard(b, \"d\"); } }",
        // try / catch / finally.
        "static int P(int x) { var t = Make(1); try { if (x > 1) return 1; x++; } finally { Sink(t); } return x; }",
        "static void P() { var t = Make(1); try { Work(); } finally { Sink(t); } }",
        "static void P() { var t = Make(1); try { Sink(t); } catch (Exception) { Work(); } }",
        "static void P() { try { var t = Make(1); Sink(t); } catch (Exception) { Work(); } }",
        "static void P() { var t = Make(1); try { Sink(t); } finally { Work(); } }",
        "static void P() { var t = Make(1); try { Work(); } catch (Exception) { Obligation.Discard(t, \"x\"); throw; } Sink(t); }",
        "static void P() { var t = Make(1); try { Work(); } catch (Exception) { Sink(t); return; } Sink(t); }",
        "static void P() { var t = Make(1); try { try { Work(); } finally { Sink(t); } } finally { Work(); } }",
        "static int P() { var a = Make(1); var b = Make(2); try { try { return 1; } finally { Sink(a); } } finally { Sink(b); } }",
        "static void P() { var t = Make(1); try { Work(); } catch (InvalidOperationException) { Work(); } finally { Sink(t); } }",
        "static void P(int n) { for (var i = 0; i < n; i++) { var t = Make(i); try { if (Check()) continue; if (Check()) break; Work(); } finally { Sink(t); } } }",
        "static void P(int n) { for (var i = 0; i < n; i++) { try { var t = Make(i); Sink(t); } catch (Exception) { Work(); } } }",
        "static async Task P() { var t = Make(1); try { await Task.Delay(1); } finally { Sink(t); } }",
        // Out parameters hand a token to the caller.
        "static void P() { Make2(out var t); Sink(t); }",
        "static void P(out Token made) { made = Make(1); }",
        // A local function that consumes a captured token, called once or on exclusive branches.
        "static void P() { var t = Make(1); Finish(); void Finish() => Sink(t); }",
        "static void P(bool c) { var t = Make(1); if (c) { Finish(); } else { Sink(t); } void Finish() => Sink(t); }",
        "static int P() { var t = Make(1); var id = Peek(); Sink(t); return id; int Peek() => t.Id; }",
        // Async sinks that are awaited, returned or waited for.
        "static async Task P() { var t = Make(1); await SinkAsync(t); }",
        "static Task P() { var t = Make(1); return SinkAsync(t); }",
        "static async Task P() { var t = Make(1); await SinkAsync(t).ConfigureAwait(false); }",
        "static async Task P() { var a = Make(1); var b = Make(2); await Task.WhenAll(SinkAsync(a), SinkAsync(b)); }",
        // Fresh tokens each pass of a loop.
        "static void P(int n) { for (var i = 0; i < n; i++) { var t = Make(i); Sink(t); } }",
        // A sink that hands its own parameter on exactly once, on each branch.
        "[ConsumesObligation] static void P(Token t, bool c) { if (c) { Sink(t); } else { Obligation.Discard(t, \"d\"); } }",
        // Reading a token is not consuming it.
        "static int P() { var t = Make(1); var id = t.Id; Sink(t); return id; }",
    };

    [Theory]
    [MemberData(nameof(CorrectPrograms))]
    public async Task CorrectProgram_IsNotReported(string member)
    {
        Assert.Empty(await Run(member));
    }

    public static TheoryData<string, string> WrongPrograms => new()
    {
        // A second consumption after a partial one, in a loop, and through a conditional argument.
        { "static void P(bool c) { var t = Make(1); if (c) { Sink(t); } Sink(t); }", "may be consumed more than once" },
        { "static void P(int n) { var t = Make(1); var i = 0; do { Sink(t); i++; } while (i < n); }", "may be consumed more than once" },
        { "static void P(bool c) { var a = Make(1); var b = Make(2); Sink(c ? a : b); Sink(a); Sink(b); }", "may be consumed more than once" },
        { "static void P(bool c) { var a = Make(1); var b = Make(2); Sink(c ? a : b); }", "may not be consumed on all execution paths" },
        // A sink's own parameter passed on twice.
        { "[ConsumesObligation] static void P(Token t) { Sink(t); Sink(t); }", "may be consumed more than once" },
        { "[ConsumesObligation] static void P(Token t, bool c) { if (c) Sink(t); Sink(t); }", "may be consumed more than once" },
        // Catch and finally blocks are visited.
        { "static int P(string s) { var t = Make(1); Sink(t); try { return int.Parse(s); } catch (FormatException) { Sink(t); return 0; } }", "was consumed more than once" },
        { "static void P() { var t = Make(1); try { Sink(t); } finally { Sink(t); } }", "was consumed more than once" },
        { "static void P() { var t = Make(1); try { Sink(t); Work(); } catch (Exception) { Sink(t); } }", "was consumed more than once" },
        { "static void P() { var t = Make(1); try { Work(); } finally { Sink(t); Sink(t); } }", "was consumed more than once" },
        { "static void P() { var t = Make(1); try { Work(); } catch (Exception) { Sink(t); } finally { Sink(t); } }", "was consumed more than once" },
        { "static void P() { var t = Make(1); try { Work(); Sink(t); } catch (Exception) { Work(); } }", "may not be consumed on all execution paths" },
        { "static void P() { try { Work(); } catch (Exception) { var q = Make(1); Work(); } }", "was never consumed before returning" },
        { "static void P() { try { Work(); } finally { var q = Make(1); } }", "was never consumed before returning" },
        { "static int P(int x) { var t = Make(1); try { if (x > 1) return 1; Sink(t); } finally { Work(); } return x; }", "was never consumed before returning" },
        { "static void P(int n) { for (var i = 0; i < n; i++) { var t = Make(i); try { if (Check()) break; Sink(t); } finally { Work(); } } }", "was never consumed before returning" },
        { "static void P() { var t = Make(1); try { Sink(t); } catch (Exception) { Obligation.Discard(t, \"x\"); } }", "was consumed more than once" },
        // Passing a token to a method that is not a sink.
        { "static void Finish(Token t) { Sink(t); } static void P() { var t = Make(1); Finish(t); Sink(t); }", "which is not a `[ConsumesObligation]` method" },
        { "static T Same<T>(T v) => v; static void P() { var p = Make(1); var q = Same(p); Sink(q); Sink(p); }", "which is not a `[ConsumesObligation]` method" },
        { "static void P() { var l = new List<Token>(); var t = Make(1); l.Add(t); Sink(t); }", "which is not a `[ConsumesObligation]` method" },
        { "static void Keep(Token t) { } static void P() { Keep(Make(1)); }", "which is not a `[ConsumesObligation]` method" },
        // Tokens in places the analyzer cannot follow.
        { "static void P() { var t = Make(1); var batch = new[] { t }; Sink(t); Sink(batch[0]); }", "was put in an array" },
        { "static void P() { var arr = new Token[1]; arr[0] = Make(1); }", "was stored in a field, property or array element" },
        { "static int P() { var pair = (Make(1), 1); return pair.Item2; }", "was stored in a variable of type" },
        { "static void P() { var pair = MakePair(); Work(); }", "was stored in a variable of type" },
        { "static void P() { var t = Make(1); Take((t, 1)); } static void Take((Token, int) x) { }", "was put in a tuple" },
        { "static bool P() { Token? held = Make(1); return held.HasValue; }", "was stored in a variable of type `PathDomain.Token?`" },
        { "static void P() { var t = Make(1); Token? held = t; Sink(t); }", "was stored in a variable of type" },
        { "static int P() { var id = Make(1).Id; return id; }", "was produced as a temporary and dropped" },
        { "static int P() { return Make(1).Id; }", "was produced as a temporary and dropped" },
        // Async sinks whose task is dropped.
        { "static void P() { var t = Make(1); _ = SinkAsync(t); }", "the returned task is not awaited or returned" },
        { "static void P() { var t = Make(1); SinkAsync(t); }", "the returned task is not awaited or returned" },
        // Out parameters.
        { "static void P() { Make2(out var t); }", "was never consumed before returning" },
        { "static void P(out Token made) { made = Make(1); made = Make(2); }", "was overwritten before its previous obligation was consumed" },
        { "static void P() { Make2(out _); }", "was explicitly discarded" },
        // Captured tokens and local functions.
        { "static void P() { var t = Make(1); Finish(); Finish(); void Finish() => Sink(t); }", "was consumed more than once" },
        { "static void P() { var t = Make(1); Sink(t); Finish(); void Finish() => Sink(t); }", "was consumed more than once" },
        { "static void P(bool c) { var t = Make(1); Finish(c); void Finish(bool x) { if (x) Sink(t); } }", "may not be consumed on all execution paths" },
        { "static void P() { var t = Make(1); Finish(); } static void Finish() { }", "was never consumed before returning" },
        // Tuple swap does not hide a leak.
        { "static void P() { var a = Make(1); var b = Make(2); (a, b) = (b, a); Sink(a); }", "was never consumed before returning" },
        // Moving a token into a second variable leaves the first one consumed.
        { "static void P() { var p = Make(1); var q = p; Sink(p); Sink(q); }", "was consumed more than once" },
        { "static void P() { var p = Make(1); Sink(p); var q = p; Sink(q); }", "was already consumed before assignment" },
    };

    [Theory]
    [MemberData(nameof(WrongPrograms))]
    public async Task WrongProgram_IsReportedForTheRightReason(string member, string fragment)
    {
        var reported = await Run(member);
        Assert.Contains(reported, line => line.Contains(fragment, StringComparison.Ordinal));
    }

    [Fact]
    public async Task NonSinkPass_IsReportedOnce_NotAlsoAsALeak()
    {
        var reported = await Run("static void Finish(Token t) { Sink(t); } static void P() { var t = Make(1); Finish(t); }");
        Assert.Single(reported);
        Assert.Contains("which is not a `[ConsumesObligation]` method", reported[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task CatchBlock_StartsFromTheStateInsideTheTry_NotFromTheLocalsOfTheTry()
    {
        // The token is made inside the try, so the catch block cannot see it and must not report it as maybe-live.
        var reported = await Run("static void P() { try { var t = Make(1); Work(); Sink(t); } catch (Exception) { Work(); } }");
        Assert.Empty(reported);
    }

    private const string MemberPreamble = """
        using System;
        using Verifiable.Contracts;

        namespace MemberDomain;

        [MustConsume]
        public readonly record struct Tok(int Id);

        [MustConsume]
        public readonly record struct Scoped(int Id) : IDisposable
        {
            [ConsumesObligation]
            public void Dispose() { _ = Id; }
        }

        [MustConsume]
        public readonly record struct Plain(int Id) : IDisposable
        {
            public void Dispose() { }
        }

        public static class Sinks
        {
            [ConsumesObligation]
            public static void Sink(Tok t) { _ = t.Id; }
        }

        """;

    private static async Task<string[]> RunMembers(string body)
    {
        var diags = await AnalyzerTestHarness.AnalyzeAsync(MemberPreamble + body, new LinearObligationAnalyzer());
        Assert.DoesNotContain(diags, d => d.Id == "AD0001");
        Assert.True(!diags.Any(d => d.Id.StartsWith("CS") && d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error), string.Join("|", diags.Where(d => d.Id.StartsWith("CS")).Select(d => d.ToString())));
        return diags.Where(d => d.Id == "VER007").Select(d => d.GetMessage().Split('\n')[0].TrimEnd('\r')).ToArray();
    }

    [Theory]
    [InlineData("public sealed class H { public Tok T { get; set; } public static void P(H h) { Sinks.Sink(h.T); } }", "cannot tell")]
    [InlineData("public sealed class H { public Tok T; public static void P(H h) { var t = h.T; Sinks.Sink(t); } }", "cannot tell")]
    [InlineData("public static class H { public static Tok T; public static void P() { Sinks.Sink(T); } }", "cannot tell")]
    [InlineData("public static class H { public static readonly Tok T = new(1); }", "initializer")]
    [InlineData("public sealed class H { public Tok T { get; } = new(1); }", "initializer")]
    [InlineData("public sealed class H { public Tok T = new(1); }", "initializer")]
    public async Task TokenInMember_IsRejected(string members, string fragment)
    {
        var reported = await RunMembers(members);
        Assert.Contains(reported, line => line.Contains(fragment, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("public sealed class H { public Tok T; public static int P(H h) => h.T.Id; }")]
    [InlineData("public static class H { public static void P() { using var s = new Scoped(1); _ = s.Id; } }")]
    [InlineData("public static class H { public static void P() { var s = new Scoped(1); using (s) { _ = s.Id; } } }")]
    public async Task MemberShapes_ThatAreFine_AreNotReported(string members)
    {
        var r = await RunMembers(members);
        Assert.True(r.Length == 0, string.Join("|", r));
    }

    [Theory]
    [InlineData("public static class H { [ConsumesObligation] public static void Done(Scoped s) => s.Dispose(); public static void P() { var s = new Scoped(1); using (s) { Done(s); } } }")]
    [InlineData("public static class H { public static void P() { using var s = new Plain(1); } }")]
    [InlineData("public static class H { public static void P(bool b) { using var s = new Scoped(1); if (b) { return; } s.Dispose(); } }")]
    public async Task UsingOnAToken_IsAnImplicitDisposeCall(string members)
    {
        Assert.NotEmpty(await RunMembers(members));
    }

}
