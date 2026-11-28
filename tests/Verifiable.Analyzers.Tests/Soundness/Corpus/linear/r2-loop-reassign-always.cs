// Right: the loop commits then replaces the token each iteration, and commits the last one after.
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

public static class LoopReassignOk
{
    public static PendingCommit Create(int id) => new(id);

    [ConsumesObligation]
    public static void Commit(PendingCommit c) => Ledger.Record(c.Id);

    public static void Run(int n)
    {
        var p = Create(1);
        while (n-- > 0)
        {
            Commit(p);
            p = Create(n + 10);
        }
        Commit(p);
    }
}
