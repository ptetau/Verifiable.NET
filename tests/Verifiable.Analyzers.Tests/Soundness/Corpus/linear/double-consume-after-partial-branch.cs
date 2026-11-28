// Wrong: when `first` is true the token is committed on the branch and then again after the merge.
// @expect: rejected
// @witness: Corpus.Linear.PartialThenAgain.Run(true)
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

// Not [Verifiable]: the ledger needs mutable state, and VER007 checks every method in the compilation anyway.
public static class PartialThenAgain
{
    public static PendingCommit Create(int id) => new(id);

    [ConsumesObligation]
    public static void Commit(PendingCommit c) => Ledger.Record(c.Id);

    public static void Run(bool first)
    {
        var p = Create(1);
        if (first)
        {
            Commit(p);
        }
        Commit(p);
    }
}
