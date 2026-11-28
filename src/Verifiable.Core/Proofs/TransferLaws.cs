namespace Verifiable.Core.Proofs;

/// <summary>
/// Lemmas about the <c>VER007</c> transfer rules in <see cref="LinearTransfer"/>. Each method's postcondition
/// says "this law always holds"; Z3 checks it for every combination of states when the project builds.
/// </summary>
/// <remarks>
/// <para>
/// The order is the one the join defines: <c>a ≤ b</c> exactly when <c>JoinCore(a, b) == b</c>. Consumed and
/// Live sit at the bottom, MaybeLive above both, and SinkParameter at the top.
/// </para>
/// <para>
/// Monotone and distributive transfer rules, together with the semilattice laws in <see cref="LatticeLaws"/>,
/// mean the worklist reaches the same fixed point in any visiting order, and that fixed point is exactly the
/// merge of what each path alone would give. Reading a token and the exit check leave the state unchanged,
/// so they are monotone trivially.
/// </para>
/// <para>
/// The rank lemmas bound the work: a merge that changes a state raises its rank, and a rank is at most 2.
/// So a block's incoming state changes at most once per new token plus twice per token, which is the budget
/// the analyzer gives its worklist.
/// </para>
/// </remarks>
internal static class TransferLaws
{
    private static bool Below(ObligationState a, ObligationState b) => ObligationLattice.JoinCore(a, b) == b;

    private static int Rank(ObligationState s) =>
        s == ObligationState.SinkParameter ? 2 : (s == ObligationState.MaybeLive ? 1 : 0);

    internal static bool ConsumeIsMonotone(ObligationState a, ObligationState b)
    {
        Spec.Ensures<bool>(r => r, "Consuming a token that is live on more paths never gives a state below consuming it on fewer.");
        return !Below(a, b) || Below(LinearTransfer.Consume(a), LinearTransfer.Consume(b));
    }

    internal static bool DiscardIsMonotone(ObligationState a, ObligationState b)
    {
        Spec.Ensures<bool>(r => r, "Discarding a token that is live on more paths never gives a state below discarding it on fewer.");
        return !Below(a, b) || Below(LinearTransfer.Discard(a), LinearTransfer.Discard(b));
    }

    internal static bool OverwriteIsMonotone(ObligationState a, ObligationState b)
    {
        Spec.Ensures<bool>(r => r, "Overwriting a variable gives the same state whatever it held, so it is monotone.");
        return !Below(a, b) || Below(LinearTransfer.Overwrite(a), LinearTransfer.Overwrite(b));
    }

    internal static bool ConsumeDistributesOverJoin(ObligationState a, ObligationState b)
    {
        Spec.Ensures<bool>(r => r, "Consuming after two paths merge gives the same state as consuming on each path and then merging.");
        return LinearTransfer.Consume(ObligationLattice.JoinCore(a, b)) ==
               ObligationLattice.JoinCore(LinearTransfer.Consume(a), LinearTransfer.Consume(b));
    }

    internal static bool DiscardDistributesOverJoin(ObligationState a, ObligationState b)
    {
        Spec.Ensures<bool>(r => r, "Discarding after two paths merge gives the same state as discarding on each path and then merging.");
        return LinearTransfer.Discard(ObligationLattice.JoinCore(a, b)) ==
               ObligationLattice.JoinCore(LinearTransfer.Discard(a), LinearTransfer.Discard(b));
    }

    internal static bool OverwriteDistributesOverJoin(ObligationState a, ObligationState b)
    {
        Spec.Ensures<bool>(r => r, "Overwriting after two paths merge gives the same state as overwriting on each path and then merging.");
        return LinearTransfer.Overwrite(ObligationLattice.JoinCore(a, b)) ==
               ObligationLattice.JoinCore(LinearTransfer.Overwrite(a), LinearTransfer.Overwrite(b));
    }

    internal static bool ExitReportSurvivesMerging(ObligationState a, ObligationState b)
    {
        Spec.Ensures<bool>(r => r, "A token reported at exit on fewer paths is still reported on more paths, unless it becomes a sink parameter.");
        return !(Below(a, b) && b != ObligationState.SinkParameter && LinearTransfer.ExitEvent(a) != TransferEvent.Ok) ||
               LinearTransfer.ExitEvent(b) != TransferEvent.Ok;
    }

    internal static bool OverwriteReportSurvivesMerging(ObligationState a, ObligationState b)
    {
        Spec.Ensures<bool>(r => r, "An overwrite that leaks on fewer paths still leaks on more paths, unless the token becomes a sink parameter.");
        return !(Below(a, b) && b != ObligationState.SinkParameter && LinearTransfer.OverwriteEvent(a) != TransferEvent.Ok) ||
               LinearTransfer.OverwriteEvent(b) != TransferEvent.Ok;
    }

    internal static bool DoubleConsumeIsOnlySeenOnEveryPath()
    {
        Spec.Ensures<bool>(r => r, "Known gap, kept visible: once a consumed path merges with a live one, a second consumption is no longer reported.");
        return LinearTransfer.ConsumeEvent(ObligationState.Consumed) == TransferEvent.DoubleConsume &&
               Below(ObligationState.Consumed, ObligationState.MaybeLive) &&
               LinearTransfer.ConsumeEvent(ObligationState.MaybeLive) == TransferEvent.Ok;
    }

    internal static bool JoinThatChangesAStateRaisesItsRank(ObligationState a, ObligationState b)
    {
        Spec.Ensures<bool>(r => r, "Merging in another path either leaves a state alone or moves it strictly up, so the worklist cannot cycle.");
        var j = ObligationLattice.JoinCore(a, b);
        return j == a || Rank(j) > Rank(a);
    }

    internal static bool RankIsBounded(ObligationState a)
    {
        Spec.Ensures<bool>(r => r, "A state can rise at most twice, so the worklist always finishes.");
        return Rank(a) >= 0 && Rank(a) <= 2;
    }
}
