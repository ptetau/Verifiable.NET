// Wrong: one token is committed on every pass of the loop, so twice when the loop runs twice.
// @expect: rejected
// @witness: Corpus.Linear.DoWhileCommit.Run(2)
using System;
using System.Collections.Generic;
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

public static class DoWhileCommit
{
    public static PendingCommit Create(int id) => new(id);

    [ConsumesObligation]
    public static void Commit(PendingCommit c) => Ledger.Record(c.Id);

    public static void Run(int n)
    {
        var p = Create(1);
        var i = 0;
        do
        {
            Commit(p);
            i++;
        }
        while (i < n);
    }
}
