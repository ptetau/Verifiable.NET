// Wrong: a sink taking its token by in is called twice with the same token.
// @expect: rejected
// @witness: Corpus.Linear.InSink.Run()
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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

public static class InSink
{
    public static PendingCommit Create(int id) => new(id);

    [ConsumesObligation]
    public static void Commit(in PendingCommit c) => Ledger.Record(c.Id);

    public static void Run()
    {
        var p = Create(1);
        Commit(in p);
        Commit(in p);
    }
}
