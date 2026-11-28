// Wrong: when `pickFirst` is true, `a` is committed by the first call and again by the second.
// @expect: rejected
// @witness: Corpus.Linear.ConditionalArg.Run(true)
// @known-bug: `Commit(pickFirst ? a : b)` consumes a flow-capture temporary, not `a` or `b`, so both stay live and can be committed again
using System;
using System.Collections.Generic;
using Verifiable.Contracts;

namespace Corpus.Linear;

[MustConsume]
public readonly record struct PendingCommit(int Id);

public static class Ledger
{
    private static readonly HashSet<int> Committed = new();

    public static void Record(int id)
    {
        if (!Committed.Add(id)) throw new InvalidOperationException($"commit {id} applied twice");
    }
}

public static class ConditionalArg
{
    public static PendingCommit Create(int id) => new(id);

    [ConsumesObligation]
    public static void Commit(PendingCommit c) => Ledger.Record(c.Id);

    public static void Run(bool pickFirst)
    {
        var a = Create(1);
        var b = Create(2);
        Commit(pickFirst ? a : b);
        Commit(a);
        Commit(b);
    }
}
