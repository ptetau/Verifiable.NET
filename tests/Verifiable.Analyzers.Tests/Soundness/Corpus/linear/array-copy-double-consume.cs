// Wrong: storing the token in an array makes a second copy, and both copies are committed.
// @expect: rejected
// @witness: Corpus.Linear.ArrayCopy.Run()
// @known-bug: putting a token in an array neither moves nor tracks it, so the original and the array element can both be committed
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

public static class ArrayCopy
{
    public static PendingCommit Create(int id) => new(id);

    [ConsumesObligation]
    public static void Commit(PendingCommit c) => Ledger.Record(c.Id);

    public static void Run()
    {
        var p = Create(1);
        var batch = new[] { p };
        Commit(p);
        Commit(batch[0]);
    }
}
