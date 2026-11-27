namespace Verifiable.Core;

/// <summary>
/// What is known about a <c>[MustConsume]</c> token at one point in a method.
/// </summary>
[Verifiable]
internal enum ObligationState
{
    /// <summary>The token has been consumed on every path reaching this point.</summary>
    Consumed = 0,

    /// <summary>The token is live on every path reaching this point.</summary>
    Live = 1,

    /// <summary>The token is live on at least one path reaching this point.</summary>
    MaybeLive = 2,

    /// <summary>The token is a parameter of a sink method, which may hold it.</summary>
    SinkParameter = 3
}

/// <summary>
/// The join used by the <c>VER007</c> data-flow analysis when two control-flow paths meet.
/// </summary>
/// <remarks>
/// Soundness rests on two facts, both proved: a token that may still be live on either incoming path
/// is still flagged after the merge, and the join is a semilattice (see <c>Proofs/LatticeLaws.cs</c>),
/// so the worklist reaches the same fixed point whatever order blocks are visited in.
/// </remarks>
internal static class ObligationLattice
{
    /// <summary>Whether a token in this state still has to be consumed.</summary>
    internal static bool IsOutstanding(ObligationState state) =>
        state == ObligationState.Live || state == ObligationState.MaybeLive;

    /// <summary>Merges the states of one token arriving from two paths.</summary>
    internal static ObligationState Join(ObligationState a, ObligationState b)
    {
        Spec.Ensures<ObligationState>(
            r => Spec.Implies(
                (IsOutstanding(a) || IsOutstanding(b)) && a != ObligationState.SinkParameter && b != ObligationState.SinkParameter,
                IsOutstanding(r)),
            "A token that may be live on either incoming path is still flagged after the paths merge.");
        Spec.Ensures<ObligationState>(
            r => (r == ObligationState.Consumed) == (a == ObligationState.Consumed && b == ObligationState.Consumed),
            "A token counts as consumed after a merge only if it was consumed on both paths.");

        return JoinCore(a, b);
    }

    /// <summary>The join itself, kept as a single expression so lemmas can reason about it directly.</summary>
    internal static ObligationState JoinCore(ObligationState a, ObligationState b) =>
        a == b
            ? a
            : (a == ObligationState.SinkParameter || b == ObligationState.SinkParameter
                ? ObligationState.SinkParameter
                : ObligationState.MaybeLive);

    /// <summary>Merges a token tracked on one path with a path where it is not tracked at all.</summary>
    internal static ObligationState JoinWithUntracked(ObligationState present)
    {
        Spec.Ensures<ObligationState>(
            r => (r == ObligationState.Consumed) == (present == ObligationState.Consumed),
            "Merging with a path that never created the token cannot consume it or invent a consumption.");
        Spec.Ensures<ObligationState>(
            r => Spec.Implies(IsOutstanding(present), r == ObligationState.MaybeLive),
            "A live token merged with a path that never created it is at most maybe-live, and still flagged.");

        if (present == ObligationState.Consumed) return ObligationState.Consumed;
        if (present == ObligationState.SinkParameter) return ObligationState.SinkParameter;
        return ObligationState.MaybeLive;
    }
}
