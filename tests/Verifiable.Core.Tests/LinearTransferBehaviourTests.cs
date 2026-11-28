using System;
using System.Linq;
using Verifiable.Core.Proofs;
using Xunit;

namespace Verifiable.Core.Tests;

/// <summary>
/// Z3-free checks of the <c>VER007</c> transfer rules (<see cref="LinearTransfer"/>) and their lemmas
/// (<see cref="TransferLaws"/>), exhaustive over the four token states.
/// </summary>
public class LinearTransferBehaviourTests
{
    private static readonly ObligationState[] AllStates = Enum.GetValues<ObligationState>();

    private const ObligationState C = ObligationState.Consumed;
    private const ObligationState L = ObligationState.Live;
    private const ObligationState M = ObligationState.MaybeLive;
    private const ObligationState S = ObligationState.SinkParameter;

    /// <summary>The lattice order written out by hand: Consumed and Live at the bottom, then MaybeLive, then SinkParameter.</summary>
    private static bool Below(ObligationState a, ObligationState b) =>
        a == b || b == S || (b == M && a != S);

    [Fact]
    public void Order_IsTheOneTheJoinDefines()
    {
        foreach (var a in AllStates)
        {
            foreach (var b in AllStates)
            {
                Assert.Equal(Below(a, b), ObligationLattice.JoinCore(a, b) == b);
            }
        }
    }

    [Fact]
    public void Rules_MatchTheReferenceTable()
    {
        (ObligationState State, ObligationState Consume, TransferEvent ConsumeEvent, ObligationState Discard, TransferEvent DiscardEvent,
            TransferEvent OverwriteEvent, TransferEvent ReadEvent, TransferEvent ExitEvent)[] table =
        [
            (C, C, TransferEvent.DoubleConsume, C, TransferEvent.Ok, TransferEvent.Ok, TransferEvent.UseAfterConsume, TransferEvent.Ok),
            (L, C, TransferEvent.Ok, C, TransferEvent.DiscardedLive, TransferEvent.LeakOnOverwrite, TransferEvent.Ok, TransferEvent.Leak),
            (M, C, TransferEvent.Ok, C, TransferEvent.DiscardedLive, TransferEvent.LeakOnOverwrite, TransferEvent.Ok, TransferEvent.PartialLeak),
            (S, S, TransferEvent.Ok, S, TransferEvent.Ok, TransferEvent.Ok, TransferEvent.Ok, TransferEvent.Ok),
        ];

        Assert.Equal(AllStates, table.Select(row => row.State));
        foreach (var row in table)
        {
            Assert.Equal(row.Consume, LinearTransfer.Consume(row.State));
            Assert.Equal(row.ConsumeEvent, LinearTransfer.ConsumeEvent(row.State));
            Assert.Equal(row.Discard, LinearTransfer.Discard(row.State));
            Assert.Equal(row.DiscardEvent, LinearTransfer.DiscardEvent(row.State));
            Assert.Equal(L, LinearTransfer.Overwrite(row.State));
            Assert.Equal(row.OverwriteEvent, LinearTransfer.OverwriteEvent(row.State));
            Assert.Equal(row.ReadEvent, LinearTransfer.ReadEvent(row.State));
            Assert.Equal(row.ExitEvent, LinearTransfer.ExitEvent(row.State));
        }
    }

    [Fact]
    public void Entry_SinkMethodsHoldTheirParameters()
    {
        Assert.Equal(S, LinearTransfer.Entry(sinkMethod: true));
        Assert.Equal(L, LinearTransfer.Entry(sinkMethod: false));
    }

    [Fact]
    public void NextStateRules_AreMonotoneAndDistributive()
    {
        Func<ObligationState, ObligationState>[] rules = [LinearTransfer.Consume, LinearTransfer.Discard, LinearTransfer.Overwrite];
        foreach (var rule in rules)
        {
            foreach (var a in AllStates)
            {
                foreach (var b in AllStates)
                {
                    Assert.True(!Below(a, b) || Below(rule(a), rule(b)));
                    Assert.Equal(rule(ObligationLattice.JoinCore(a, b)), ObligationLattice.JoinCore(rule(a), rule(b)));
                }
            }
        }
    }

    [Fact]
    public void Lemmas_HoldForEveryState()
    {
        Assert.True(TransferLaws.DoubleConsumeIsOnlySeenOnEveryPath());
        foreach (var a in AllStates)
        {
            Assert.True(TransferLaws.RankIsBounded(a));
            foreach (var b in AllStates)
            {
                Assert.True(TransferLaws.ConsumeIsMonotone(a, b));
                Assert.True(TransferLaws.DiscardIsMonotone(a, b));
                Assert.True(TransferLaws.OverwriteIsMonotone(a, b));
                Assert.True(TransferLaws.ConsumeDistributesOverJoin(a, b));
                Assert.True(TransferLaws.DiscardDistributesOverJoin(a, b));
                Assert.True(TransferLaws.OverwriteDistributesOverJoin(a, b));
                Assert.True(TransferLaws.ExitReportSurvivesMerging(a, b));
                Assert.True(TransferLaws.OverwriteReportSurvivesMerging(a, b));
                Assert.True(TransferLaws.JoinThatChangesAStateRaisesItsRank(a, b));
            }
        }
    }

    /// <summary>
    /// The worklist bound also needs merging with a path where the token is untracked to either leave a state
    /// alone or raise it. <see cref="ObligationLattice.JoinWithUntracked"/> is not a single expression, so Z3
    /// sees only its contract; this checks the rest here. It behaves exactly like merging with a consumed token.
    /// </summary>
    [Fact]
    public void JoinWithUntracked_IsAJoinWithConsumed()
    {
        foreach (var a in AllStates)
        {
            Assert.Equal(ObligationLattice.JoinCore(a, C), ObligationLattice.JoinWithUntracked(a));
            Assert.True(Below(a, ObligationLattice.JoinWithUntracked(a)));
        }
    }
}
