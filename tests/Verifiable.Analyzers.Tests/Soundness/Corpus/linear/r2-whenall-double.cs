// Wrong: Task.WhenAll awaits two async commits of the same token.
// @expect: rejected
// @witness: Corpus.Linear.WhenAllTwice.Run()
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

public static class WhenAllTwice
{
    public static PendingCommit Create(int id) => new(id);

    [ConsumesObligation]
    public static async Task CommitAsync(PendingCommit c)
    {
        await Task.Yield();
        Ledger.Record(c.Id);
    }

    public static async Task RunAsync()
    {
        var p = Create(1);
        await Task.WhenAll(CommitAsync(p), CommitAsync(p));
    }

    public static void Run() => RunAsync().GetAwaiter().GetResult();
}
