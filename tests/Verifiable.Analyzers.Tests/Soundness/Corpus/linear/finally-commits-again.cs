// Wrong: the token is committed in the try block and again in the finally block, which always runs.
// @expect: rejected
// @witness: Corpus.Linear.FinallyAgain.Run()
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

public static class FinallyAgain
{
    public static PendingCommit Create(int id) => new(id);

    [ConsumesObligation]
    public static void Commit(PendingCommit c) => Ledger.Record(c.Id);

    public static void Run()
    {
        var p = Create(1);
        try
        {
            Commit(p);
        }
        finally
        {
            Commit(p);
        }
    }
}
