// Wrong: a ref local aliases the token, so committing through it and the original is a double commit.
// @expect: rejected
// @witness: Corpus.Linear.RefLocal.Run()
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

public static class RefLocal
{
    public static PendingCommit Create(int id) => new(id);

    [ConsumesObligation]
    public static void Commit(PendingCommit c) => Ledger.Record(c.Id);

    public static void Run()
    {
        var p = Create(1);
        ref var r = ref p;
        Commit(r);
        Commit(p);
    }
}
