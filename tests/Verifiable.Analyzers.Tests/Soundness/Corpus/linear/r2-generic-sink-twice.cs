// Wrong: a generic sink is instantiated with the token type and called twice.
// @expect: rejected
// @witness: Corpus.Linear.GenericSink.Run()
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

public static class GenericSink
{
    public static PendingCommit Create(int id) => new(id);

    [ConsumesObligation]
    public static void CommitAny<T>(T t) where T : struct { if (t is PendingCommit c) Ledger.Record(c.Id); }

    public static void Run()
    {
        var p = Create(1);
        CommitAny(p);
        CommitAny<PendingCommit>(p);
    }
}
