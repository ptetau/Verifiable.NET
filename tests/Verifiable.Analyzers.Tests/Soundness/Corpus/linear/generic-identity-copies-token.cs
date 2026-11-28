// Wrong: a generic identity function hands back a copy of the token while the original stays live, so both get committed.
// @expect: rejected
// @witness: Corpus.Linear.GenericCopy.Run()
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

public static class GenericCopy
{
    public static PendingCommit Create(int id) => new(id);

    [ConsumesObligation]
    public static void Commit(PendingCommit c) => Ledger.Record(c.Id);

    public static T Same<T>(T value) => value;

    public static void Run()
    {
        var p = Create(1);
        var q = Same(p);
        Commit(q);
        Commit(p);
    }
}
