namespace Verifiable.Core;

/// <summary>
/// What one statement's effect on a <c>[MustConsume]</c> token means for <c>VER007</c>.
/// </summary>
[Verifiable]
internal enum TransferEvent
{
    /// <summary>Nothing to report.</summary>
    Ok = 0,

    /// <summary>The token was consumed (passed to a sink, moved or returned) after it was already consumed on every path.</summary>
    DoubleConsume = 1,

    /// <summary>The token was read after it was already consumed on every path.</summary>
    UseAfterConsume = 2,

    /// <summary>The token was thrown away with <c>_ = token</c> while it may still be live.</summary>
    DiscardedLive = 3,

    /// <summary>The token's variable was assigned a new token while the old one may still be live.</summary>
    LeakOnOverwrite = 4,

    /// <summary>The method can exit here while the token is live on every path to this exit.</summary>
    Leak = 5,

    /// <summary>The method can exit here while the token is live on some, but not all, paths to this exit.</summary>
    PartialLeak = 6
}

/// <summary>
/// The per-statement transfer rules of the <c>VER007</c> analysis: how each kind of statement changes a
/// token's <see cref="ObligationState"/>, and what it reports. Each rule is a pair: a next-state function
/// and an event function. The analyzer finds the statements; these rules decide what they mean.
/// </summary>
/// <remarks>
/// <para>
/// Every next-state rule is monotone in the lattice order of <see cref="ObligationLattice.JoinCore"/>, and
/// distributes over the join (see <c>Proofs/TransferLaws.cs</c>). That is what lets the worklist reach one
/// fixed point whatever order it visits blocks in.
/// </para>
/// <para>
/// Events are read once, after the analysis has converged. Each rule's events are written for a token that
/// is tracked; a variable with no state yet (never assigned on any path) is handled by the analyzer.
/// </para>
/// </remarks>
internal static class LinearTransfer
{
    /// <summary>The state of a <c>[MustConsume]</c> parameter when the method starts.</summary>
    internal static ObligationState Entry(bool sinkMethod)
    {
        Spec.Ensures<ObligationState>(
            r => Spec.Implies(sinkMethod, r == ObligationState.SinkParameter),
            "A [ConsumesObligation] method is the place a token ends up, so it may hold its own [MustConsume] parameters.");
        Spec.Ensures<ObligationState>(
            r => Spec.Implies(!sinkMethod, r == ObligationState.Live),
            "Any other method that receives a [MustConsume] parameter owns it and must consume it.");

        return sinkMethod ? ObligationState.SinkParameter : ObligationState.Live;
    }

    /// <summary>
    /// The state after the token is consumed: passed to a <c>[ConsumesObligation]</c> method, moved into
    /// another variable, or returned.
    /// </summary>
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

    /// <summary>What consuming the token reports.</summary>
    internal static TransferEvent ConsumeEvent(ObligationState state)
    {
        Spec.Ensures<TransferEvent>(
            r => (r == TransferEvent.DoubleConsume) == (state == ObligationState.Consumed),
            "Consuming a token is a double consumption exactly when it was already consumed on every path; a sink parameter is never reported.");
        Spec.Ensures<TransferEvent>(
            r => r == TransferEvent.Ok || r == TransferEvent.DoubleConsume,
            "Consuming a token can only report a double consumption.");

        return state == ObligationState.Consumed ? TransferEvent.DoubleConsume : TransferEvent.Ok;
    }

    /// <summary>What reading a token (without consuming it) reports. Reading does not change the state.</summary>
    internal static TransferEvent ReadEvent(ObligationState state)
    {
        Spec.Ensures<TransferEvent>(
            r => (r == TransferEvent.UseAfterConsume) == (state == ObligationState.Consumed),
            "Reading a token is a use after consumption exactly when it was already consumed on every path.");
        Spec.Ensures<TransferEvent>(
            r => r == TransferEvent.Ok || r == TransferEvent.UseAfterConsume,
            "Reading a token can only report a use after consumption.");

        return state == ObligationState.Consumed ? TransferEvent.UseAfterConsume : TransferEvent.Ok;
    }

    /// <summary>The state after <c>_ = token</c>. The token is gone afterwards, so it is not reported again at exit.</summary>
    internal static ObligationState Discard(ObligationState state)
    {
        Spec.Ensures<ObligationState>(
            r => Spec.Implies(state != ObligationState.SinkParameter, r == ObligationState.Consumed),
            "A discarded token is gone, so it is reported once here and not again when the method exits.");
        Spec.Ensures<ObligationState>(
            r => Spec.Implies(state == ObligationState.SinkParameter, r == ObligationState.SinkParameter),
            "Discarding a sink method's own parameter leaves it a sink parameter.");

        return state == ObligationState.SinkParameter ? ObligationState.SinkParameter : ObligationState.Consumed;
    }

    /// <summary>What <c>_ = token</c> reports.</summary>
    internal static TransferEvent DiscardEvent(ObligationState state)
    {
        Spec.Ensures<TransferEvent>(
            r => (r == TransferEvent.DiscardedLive) == ObligationLattice.IsOutstanding(state),
            "Throwing a token away with `_` is reported exactly when it may still be live; `Obligation.Discard` is the explicit way to cancel one.");
        Spec.Ensures<TransferEvent>(
            r => r == TransferEvent.Ok || r == TransferEvent.DiscardedLive,
            "Discarding a token can only report that a live token was thrown away.");

        return ObligationLattice.IsOutstanding(state) ? TransferEvent.DiscardedLive : TransferEvent.Ok;
    }

    /// <summary>The state of a variable after a new token is assigned to it.</summary>
    internal static ObligationState Overwrite(ObligationState state)
    {
        Spec.Ensures<ObligationState>(
            r => r == ObligationState.Live,
            "A variable that has just been given a new token holds a live token, even if it was a sink parameter before.");

        return ObligationState.Live;
    }

    /// <summary>What assigning a new token to a variable reports, given the state of the token it held.</summary>
    internal static TransferEvent OverwriteEvent(ObligationState state)
    {
        Spec.Ensures<TransferEvent>(
            r => (r == TransferEvent.LeakOnOverwrite) == ObligationLattice.IsOutstanding(state),
            "Overwriting a variable leaks its old token exactly when that token may still be live.");
        Spec.Ensures<TransferEvent>(
            r => r == TransferEvent.Ok || r == TransferEvent.LeakOnOverwrite,
            "Overwriting a variable can only report a leaked old token.");

        return ObligationLattice.IsOutstanding(state) ? TransferEvent.LeakOnOverwrite : TransferEvent.Ok;
    }

    /// <summary>What a normal exit from the method reports for a token in this state.</summary>
    internal static TransferEvent ExitEvent(ObligationState state)
    {
        Spec.Ensures<TransferEvent>(
            r => (r == TransferEvent.Leak) == (state == ObligationState.Live),
            "Exiting while the token is live on every path to this exit is a leak.");
        Spec.Ensures<TransferEvent>(
            r => (r == TransferEvent.PartialLeak) == (state == ObligationState.MaybeLive),
            "Exiting while the token is live on only some paths is reported as a partial-branch leak.");
        Spec.Ensures<TransferEvent>(
            r => (r == TransferEvent.Ok) == !ObligationLattice.IsOutstanding(state),
            "A consumed token, or a sink method's own parameter, is never reported at exit.");

        return state == ObligationState.Live
            ? TransferEvent.Leak
            : (state == ObligationState.MaybeLive ? TransferEvent.PartialLeak : TransferEvent.Ok);
    }
}
