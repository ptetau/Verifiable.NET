// Right: a [ConsumesObligation] method forwards its own token on exactly one branch each, so nothing is consumed twice.
// @expect: proved
using System;
using System.Collections.Generic;
using Verifiable.Contracts;

namespace Corpus.Linear;

[MustConsume]
public readonly record struct PendingCommit(int Id);

public static class SinkForwardsOnce
{
    public static PendingCommit Create(int id) => new(id);

    [ConsumesObligation]
    public static void Commit(PendingCommit c) { _ = c.Id; }

    [ConsumesObligation]
    public static void CommitOrCancel(PendingCommit c, bool ok)
    {
        if (ok)
        {
            Commit(c);
        }
        else
        {
            Obligation.Discard(c, "cancelled");
        }
    }

    public static void Run(bool ok) => CommitOrCancel(Create(1), ok);
}
