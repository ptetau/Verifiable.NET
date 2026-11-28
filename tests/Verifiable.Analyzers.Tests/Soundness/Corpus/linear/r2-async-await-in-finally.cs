// Right: an async method awaits inside try and commits in finally, with ConfigureAwait on the await.
// @expect: proved
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

public static class AsyncFinally
{
    public static PendingCommit Create(int id) => new(id);

    [ConsumesObligation]
    public static void Commit(PendingCommit c) => Ledger.Record(c.Id);

    public static async Task Run()
    {
        var p = Create(1);
        try { await Task.Yield(); }
        finally { Commit(p); }
        await Task.Delay(1).ConfigureAwait(false);
    }
}
