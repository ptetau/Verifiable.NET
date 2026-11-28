// Right: an instance sink on another class consumes the token once.
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

public sealed class Committer
{
    [ConsumesObligation]
    public void Commit(PendingCommit c) => Ledger.Record(c.Id);
}

public static class InstSink
{
    public static PendingCommit Create(int id) => new(id);

    public static void Run(Committer k)
    {
        var p = Create(1);
        k.Commit(p);
    }
}
