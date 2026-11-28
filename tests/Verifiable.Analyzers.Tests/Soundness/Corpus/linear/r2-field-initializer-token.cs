// Wrong: a static field initializer creates a token that two methods commit.
// @expect: rejected
// @known-bug: a token held in a static readonly field can be committed from several methods without any diagnostic
// @witness: Corpus.Linear.FieldTok.Run()
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

public static class FieldTok
{
    public static readonly PendingCommit Shared = new(1);

    public static PendingCommit Create(int id) => new(id);

    [ConsumesObligation]
    public static void Commit(PendingCommit c) => Ledger.Record(c.Id);

    public static void A() => Commit(Shared);
    public static void B() => Commit(Shared);
    public static void Run() { A(); B(); }
}
