// Wrong: Finish is not a sink but must (and does) commit its parameter, and the caller commits the same token again.
// @expect: rejected
// @witness: Corpus.Linear.HelperConsumes.Run()
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

public static class HelperConsumes
{
    public static PendingCommit Create(int id) => new(id);

    [ConsumesObligation]
    public static void Commit(PendingCommit c) => Ledger.Record(c.Id);

    public static void Finish(PendingCommit c) => Commit(c);

    public static void Run()
    {
        var p = Create(1);
        Finish(p);
        Commit(p);
    }
}
