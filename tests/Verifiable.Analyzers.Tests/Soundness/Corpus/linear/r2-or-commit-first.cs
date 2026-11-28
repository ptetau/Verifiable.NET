// Right: the commit is the left operand of ||, so it always runs once.
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

public static class OrFirst
{
    public static PendingCommit Create(int id) => new(id);

    [ConsumesObligation]
    public static bool TryCommit(PendingCommit c) { Ledger.Record(c.Id); return true; }

    public static bool Run(bool other)
    {
        var p = Create(1);
        return TryCommit(p) || other;
    }
}
