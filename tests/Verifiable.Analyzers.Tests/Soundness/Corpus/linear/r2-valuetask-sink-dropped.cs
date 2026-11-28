// Wrong: a ValueTask-returning sink is called and its result dropped, so the commit may not finish.
// @expect: rejected
// @no-witness: nothing observes that the ValueTask is dropped

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

public static class VTDropped
{
    public static PendingCommit Create(int id) => new(id);

    [ConsumesObligation]
    public static async ValueTask CommitAsync(PendingCommit c)
    {
        await Task.Yield();
        _ = c.Id;
    }

    public static void Run()
    {
        var p = Create(1);
        CommitAsync(p);
    }
}
