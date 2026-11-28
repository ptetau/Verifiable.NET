// Right: two conditional arguments each build a fresh token in either branch; the sink consumes both.
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

public static class FlowOk
{
    public static PendingCommit Create(int id) => new(id);

    [ConsumesObligation]
    public static void Commit2(PendingCommit a, PendingCommit b) { Ledger.Record(a.Id); Ledger.Record(b.Id); }

    public static void Run(bool c, bool d)
    {
        Commit2(c ? Create(1) : Create(2), d ? Create(3) : Create(4));
    }
}
