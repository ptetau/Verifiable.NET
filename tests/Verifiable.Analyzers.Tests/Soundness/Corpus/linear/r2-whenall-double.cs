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
    private static readonly object Gate = new();

    // The two commits run on thread-pool threads after Task.Yield, so the set is guarded: without the lock both
    // Adds can race and return true, and the witness would miss the double commit.
    public static void Record(int id)
    {
        lock (Gate)
        {
            if (!Committed.Add(id)) throw new InvalidOperationException($"commit {id} applied twice");
        }
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
