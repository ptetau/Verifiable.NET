// Wrong: an extension-method sink is called twice on the same token.
// @expect: rejected
// @witness: Corpus.Linear.ExtSink.Run()
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

public static class Ext
{
    [ConsumesObligation]
    public static void Done(this PendingCommit c) => Ledger.Record(c.Id);
}

public static class ExtSink
{
    public static PendingCommit Create(int id) => new(id);

    public static void Run()
    {
        var p = Create(1);
        p.Done();
        p.Done();
    }
}
