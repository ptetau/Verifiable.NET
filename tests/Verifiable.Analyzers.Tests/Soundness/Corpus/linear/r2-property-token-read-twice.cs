// Wrong: a property hands out the same token on every read, and each read is committed.
// @expect: rejected
// @witness: Corpus.Linear.PropTok.Run()
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

public sealed class Holder
{
    public PendingCommit Tok { get; } = new(1);
}

public static class PropTok
{
    public static PendingCommit Create(int id) => new(id);

    [ConsumesObligation]
    public static void Commit(PendingCommit c) => Ledger.Record(c.Id);

    public static void Run()
    {
        var h = new Holder();
        Commit(h.Tok);
        Commit(h.Tok);
    }
}
