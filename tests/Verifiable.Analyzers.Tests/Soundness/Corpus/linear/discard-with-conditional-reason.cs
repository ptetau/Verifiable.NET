// Right: the token is discarded exactly once; only the reason text is chosen by a conditional.
// @expect: proved
using System;
using System.Collections.Generic;
using Verifiable.Contracts;

namespace Corpus.Linear;

[MustConsume]
public readonly record struct PendingCommit(int Id);

public static class DiscardConditionalReason
{
    public static PendingCommit Create(int id) => new(id);

    public static void Run(bool timedOut)
    {
        var p = Create(1);
        Obligation.Discard(p, timedOut ? "timed out" : "cancelled");
    }
}
