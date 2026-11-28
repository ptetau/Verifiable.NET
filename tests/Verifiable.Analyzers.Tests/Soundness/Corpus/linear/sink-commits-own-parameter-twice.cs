// Wrong: a [ConsumesObligation] method passes its own token on to Commit twice.
// @expect: rejected
// @witness: Corpus.Linear.SinkTwice.Run()
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

public static class SinkTwice
{
    public static PendingCommit Create(int id) => new(id);

    [ConsumesObligation]
    public static void Commit(PendingCommit c) => Ledger.Record(c.Id);

    [ConsumesObligation]
    public static void CommitAndAudit(PendingCommit c)
    {
        Commit(c);
        Commit(c);
    }

    public static void Run() => CommitAndAudit(Create(1));
}
