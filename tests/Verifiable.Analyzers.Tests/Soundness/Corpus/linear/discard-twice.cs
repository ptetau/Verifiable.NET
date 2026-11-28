// Wrong: the same token is cancelled twice with Obligation.Discard.
// @expect: rejected
// @no-witness: Obligation.Discard does nothing at run time, so the second discard cannot be observed
using System;
using System.Collections.Generic;
using Verifiable.Contracts;

namespace Corpus.Linear;

[MustConsume]
public readonly record struct PendingCommit(int Id);

public static class DiscardTwice
{
    public static PendingCommit Create(int id) => new(id);

    public static void Run()
    {
        var p = Create(1);
        Obligation.Discard(p, "cancelled");
        Obligation.Discard(p, "cancelled again");
    }
}
