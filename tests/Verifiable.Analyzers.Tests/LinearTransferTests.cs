using System;
using System.Linq;
using System.Threading.Tasks;
using Verifiable.Core;
using Xunit;

namespace Verifiable.Analyzers.Tests;

/// <summary>
/// The <c>VER007</c> per-statement transfer rules, now in the kernel (src/Verifiable.Core/LinearTransfer.cs):
/// their proofs can fail, they make the same decisions the analyzer used to make inline, and the analyzer
/// still reports what it reported before.
/// </summary>
public class LinearTransferTests
{
    private static readonly ObligationState[] AllStates = Enum.GetValues<ObligationState>();

    // ---------------------------------------------------------------------------------------------
    // Break tests: each one breaks a transfer rule on purpose and checks that its proof fails.
    // ---------------------------------------------------------------------------------------------

    private const string ConsumeRule = """
            internal static ObligationState Consume(ObligationState state)
            {
                Spec.Ensures<ObligationState>(
                    r => Spec.Implies(state != ObligationState.SinkParameter, r == ObligationState.Consumed),
                    "After a token is consumed it counts as consumed on every path, whatever it was before.");
                Spec.Ensures<ObligationState>(
                    r => Spec.Implies(state == ObligationState.SinkParameter, r == ObligationState.SinkParameter),
                    "A sink method's own parameter stays a sink parameter when it is passed on, so it is never reported.");

                return state == ObligationState.SinkParameter ? ObligationState.SinkParameter : ObligationState.Consumed;
            }
        """;

    // A consume rule rewritten together with its contract, so only the monotonicity lemma can object:
    // a sink parameter that is passed on would come back live, below what a live token turns into.
    private const string NonMonotoneConsumeRule = """
            internal static ObligationState Consume(ObligationState state)
            {
                return state == ObligationState.SinkParameter ? ObligationState.Live : ObligationState.Consumed;
            }
        """;

    public static TheoryData<string, string, string, string> TransferBugs => new()
    {
        { "LinearTransfer.cs", "return sinkMethod ? ObligationState.SinkParameter : ObligationState.Live;", "return ObligationState.SinkParameter;", "LinearTransfer.Entry" },
        { "LinearTransfer.cs", "return state == ObligationState.SinkParameter ? ObligationState.SinkParameter : ObligationState.Consumed;\n    }\n\n    /// <summary>What consuming", "return state == ObligationState.SinkParameter ? ObligationState.SinkParameter : ObligationState.Live;\n    }\n\n    /// <summary>What consuming", "LinearTransfer.Consume" },
        { "LinearTransfer.cs", "return state == ObligationState.Consumed ? TransferEvent.DoubleConsume : TransferEvent.Ok;", "return state == ObligationState.Consumed || state == ObligationState.SinkParameter ? TransferEvent.DoubleConsume : TransferEvent.Ok;", "LinearTransfer.ConsumeEvent" },
        { "LinearTransfer.cs", "return state == ObligationState.Consumed ? TransferEvent.UseAfterConsume : TransferEvent.Ok;", "return state != ObligationState.Live ? TransferEvent.UseAfterConsume : TransferEvent.Ok;", "LinearTransfer.ReadEvent" },
        { "LinearTransfer.cs", "return state == ObligationState.SinkParameter ? ObligationState.SinkParameter : ObligationState.Consumed;\n    }\n\n    /// <summary>What <c>_ = token</c>", "return state;\n    }\n\n    /// <summary>What <c>_ = token</c>", "LinearTransfer.Discard" },
        { "LinearTransfer.cs", "return ObligationLattice.IsOutstanding(state) ? TransferEvent.DiscardedLive : TransferEvent.Ok;", "return state == ObligationState.Live ? TransferEvent.DiscardedLive : TransferEvent.Ok;", "LinearTransfer.DiscardEvent" },
        { "LinearTransfer.cs", "return ObligationState.Live;", "return state;", "LinearTransfer.Overwrite" },
        { "LinearTransfer.cs", "return ObligationLattice.IsOutstanding(state) ? TransferEvent.LeakOnOverwrite : TransferEvent.Ok;", "return state == ObligationState.Live ? TransferEvent.LeakOnOverwrite : TransferEvent.Ok;", "LinearTransfer.OverwriteEvent" },
        { "LinearTransfer.cs", ": (state == ObligationState.MaybeLive ? TransferEvent.PartialLeak : TransferEvent.Ok);", ": TransferEvent.Ok;", "LinearTransfer.ExitEvent" },
        { "LinearTransfer.cs", ": (state == ObligationState.MaybeLive ? TransferEvent.PartialLeak : TransferEvent.Ok);", ": (state == ObligationState.Consumed ? TransferEvent.Ok : TransferEvent.PartialLeak);", "LinearTransfer.ExitEvent" },
        { "LinearTransfer.cs", ConsumeRule.Replace("\r\n", "\n"), NonMonotoneConsumeRule.Replace("\r\n", "\n"), "TransferLaws.ConsumeIsMonotone" },
    };

    [Theory]
    [MemberData(nameof(TransferBugs))]
    public Task BrokenTransferRule_FailsItsProof(string file, string original, string broken, string expectedSymbol) =>
        new KernelSelfVerificationTests().BrokenKernel_FailsItsProof(file, original, broken, expectedSymbol);

    // ---------------------------------------------------------------------------------------------
    // Differential check: the kernel rules make the same decisions as the inline analyzer code they replaced.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The decisions <c>LinearObligationAnalyzer</c> made inline before they moved into the kernel, copied
    /// from it. Each returns the next state and whether that site reported a diagnostic.
    /// </summary>
    private static class OldInline
    {
        // AnalyzeCfg: entryState[p] = isSinkMethod ? SinkParameter : Live;
        public static ObligationState Entry(bool isSinkMethod) =>
            isSinkMethod ? ObligationState.SinkParameter : ObligationState.Live;

        // The same shape at all three consuming sites: AssignToLinearTarget (moving the source),
        // a [ConsumesObligation] argument, and ConsumeForReturn. The first consumption site is recorded
        // only in the second branch.
        public static (ObligationState Next, bool Reported, bool SiteRecorded) Consume(ObligationState cur)
        {
            if (cur == ObligationState.Consumed) return (cur, true, false);
            if (cur != ObligationState.SinkParameter) return (ObligationState.Consumed, false, true);
            return (cur, false, false);
        }

        // HandleSimpleAssignment, `_ = token` on a tracked token.
        public static (ObligationState Next, bool Reported) Discard(ObligationState curSt) =>
            curSt is ObligationState.Live or ObligationState.MaybeLive
                ? (ObligationState.Consumed, true)
                : (curSt, false);

        // AssignFreshLinearTarget on a variable that already has a state.
        public static (ObligationState Next, bool Reported) Overwrite(ObligationState existing) =>
            (ObligationState.Live, existing is ObligationState.Live or ObligationState.MaybeLive);

        // WalkNonAssigningReads: a read reports when cur == Consumed.
        public static bool Read(ObligationState cur) => cur == ObligationState.Consumed;

        // CheckReturnBranchLeaks: Live or MaybeLive reports; MaybeLive is the partial-branch message.
        public static (bool Reported, bool Partial) Exit(ObligationState value) =>
            (value is ObligationState.Live or ObligationState.MaybeLive, value == ObligationState.MaybeLive);
    }

    [Fact]
    public void KernelRules_AgreeWithTheOldInlineLogic_ForEveryState()
    {
        foreach (var sink in new[] { false, true })
        {
            Assert.Equal(OldInline.Entry(sink), LinearTransfer.Entry(sink));
        }

        foreach (var s in AllStates)
        {
            var consume = OldInline.Consume(s);
            Assert.Equal(consume.Next, LinearTransfer.Consume(s));
            Assert.Equal(consume.Reported ? TransferEvent.DoubleConsume : TransferEvent.Ok, LinearTransfer.ConsumeEvent(s));
            Assert.Equal(consume.SiteRecorded, LinearTransfer.Consume(s) != s); // the analyzer records the site on a change

            var discard = OldInline.Discard(s);
            Assert.Equal(discard.Next, LinearTransfer.Discard(s));
            Assert.Equal(discard.Reported ? TransferEvent.DiscardedLive : TransferEvent.Ok, LinearTransfer.DiscardEvent(s));

            var overwrite = OldInline.Overwrite(s);
            Assert.Equal(overwrite.Next, LinearTransfer.Overwrite(s));
            Assert.Equal(overwrite.Reported ? TransferEvent.LeakOnOverwrite : TransferEvent.Ok, LinearTransfer.OverwriteEvent(s));

            Assert.Equal(OldInline.Read(s) ? TransferEvent.UseAfterConsume : TransferEvent.Ok, LinearTransfer.ReadEvent(s));

            var exit = OldInline.Exit(s);
            var expectedExit = !exit.Reported ? TransferEvent.Ok : exit.Partial ? TransferEvent.PartialLeak : TransferEvent.Leak;
            Assert.Equal(expectedExit, LinearTransfer.ExitEvent(s));
        }
    }

    // ---------------------------------------------------------------------------------------------
    // End to end: what the analyzer reports for each transfer rule.
    // ---------------------------------------------------------------------------------------------

    private const string Preamble = """
        using Verifiable.Contracts;

        namespace LinearDomain;

        [MustConsume]
        public readonly record struct Token(int Id);

        public static class Flow
        {
            public static Token Make(int id) => new(id);

            [ConsumesObligation]
            public static void Sink(Token t) { }

        """;

    private const string Postamble = """

        }
        """;

    private const string NeverConsumed = "was never consumed before returning from this method";
    private const string MaybeNotConsumed = "may not be consumed on all execution paths before returning";

    public static TheoryData<string, string[]> Reports => new()
    {
        { "static void P(int id) { var t = Make(id); Sink(t); }", [] },
        { "static void P(int id) { var t = Make(id); Sink(t); Sink(t); }", ["was consumed more than once"] },
        { "static void P(Token t) { Sink(t); Sink(t); }", ["was consumed more than once"] },
        { "static void P(int id) { var t = Make(id); Sink(t); var u = t; Sink(u); }", ["was already consumed before assignment"] },
        { "static Token P(int id) { var t = Make(id); return t; }", [] },
        { "static Token P(int id) { var t = Make(id); Sink(t); return t; }", ["was already consumed before being returned"] },
        { "static void P(int id) { var t = Make(id); Sink(t); var n = t.Id; }", ["was used after being consumed or moved"] },
        { "static void P(int id) { var t = Make(id); var n = t.Id; Sink(t); }", [] },
        { "static void P(int id) { var t = Make(id); _ = t; }", ["was explicitly discarded with `_` without being consumed"] },
        { "static void P(bool c, int id) { var t = Make(id); if (c) Sink(t); _ = t; }", ["was explicitly discarded with `_` without being consumed"] },
        { "static void P(int id) { var t = Make(id); Sink(t); _ = t; }", [] },
        { "static void P(int id) { var t = Make(id); t = Make(id + 1); Sink(t); }", ["was overwritten before its previous obligation was consumed"] },
        { "static void P(bool c, int id) { var t = Make(id); if (c) Sink(t); t = Make(id + 1); Sink(t); }", ["was overwritten before its previous obligation was consumed"] },
        { "static void P(int id) { var t = Make(id); Sink(t); t = Make(id + 1); Sink(t); }", [] },
        { "static void P(int id) { var t = Make(id); }", [NeverConsumed] },
        { "static void P(Token t) { }", [NeverConsumed] },
        { "static void P(bool c, int id) { var t = Make(id); while (c) { Sink(t); } }", [MaybeNotConsumed] },
        { "[ConsumesObligation] static void P(Token t) { }", [] },
        { "[ConsumesObligation] static void P(Token t) { Sink(t); Sink(t); }", [] },
        { "[ConsumesObligation] static Token P(Token t) { return t; }", [] },
        { "[ConsumesObligation] static void P(Token t) { var u = t; Sink(u); }", [] },
        { "[ConsumesObligation] static void P(Token t, int id) { t = Make(id); }", [NeverConsumed] },
        // Known gap (see TransferLaws.DoubleConsumeIsOnlySeenOnEveryPath): consumed on one path, then again.
        { "static void P(bool c, int id) { var t = Make(id); if (c) Sink(t); Sink(t); }", [] },
    };

    [Theory]
    [MemberData(nameof(Reports))]
    public async Task Analyzer_ReportsWhatTheTransferRulesSay(string member, string[] expected)
    {
        var diags = await AnalyzerTestHarness.AnalyzeAsync(Preamble + member + Postamble, new LinearObligationAnalyzer());

        Assert.DoesNotContain(diags, d => d.Id == "AD0001");
        var reported = diags
            .Where(d => d.Id == "VER007")
            .Select(d => d.GetMessage().Split('\n')[0].TrimEnd('\r'))
            .Where(line => line.StartsWith("Linear obligation token `t` (`Token`) ", StringComparison.Ordinal))
            .Select(line => line["Linear obligation token `t` (`Token`) ".Length..].TrimEnd('.'))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expected.OrderBy(x => x, StringComparer.Ordinal).ToArray(), reported);
    }

    /// <summary>
    /// The worklist used to stop after a fixed 2000 block visits. A long method could hit that before the
    /// analysis reached its exit, and its leak was then silently not reported.
    /// </summary>
    [Fact]
    public async Task LongMethod_StillReachesTheFixedPoint()
    {
        var member = "static void P(bool c, int id) { var t = Make(id); var k = 0;\n" +
                     string.Concat(Enumerable.Repeat("if (c) k++;\n", 1500)) +
                     "}";

        var diags = await AnalyzerTestHarness.AnalyzeAsync(Preamble + member + Postamble, new LinearObligationAnalyzer());

        Assert.DoesNotContain(diags, d => d.Id == "AD0001");
        var leak = Assert.Single(diags, d => d.Id == "VER007");
        Assert.Contains(NeverConsumed, leak.GetMessage());
    }
}
