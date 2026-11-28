// Right: a null-conditional member read of a struct token does not consume it; the commit does, once.
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

[MustConsume]
public readonly record struct Named(int Id, string? Name);

public static class NullCond
{
    [ConsumesObligation]
    public static void Commit(Named h) => Ledger.Record(h.Id);

    public static int Run()
    {
        var h = new Named(1, null);
        var id = h.Name?.Length ?? 0;
        Commit(h);
        return id;
    }
}
