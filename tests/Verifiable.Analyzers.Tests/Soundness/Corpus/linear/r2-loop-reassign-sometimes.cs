// Wrong: the loop commits every iteration but only sometimes makes a fresh token, so it commits one twice.
// @expect: rejected
// @witness: Corpus.Linear.LoopReassign.Run(2)
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

public static class LoopReassign
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
            if (n % 2 == 0) p = Create(n + 10);
        }
    }
}
