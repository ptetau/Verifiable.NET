// Right: the catch commits and rethrows; the normal path commits after the try.
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

public static class CatchRethrow
{
    public static PendingCommit Create(int id) => new(id);

    [ConsumesObligation]
    public static void Commit(PendingCommit c) => Ledger.Record(c.Id);

    public static void Work(int x) { if (x < 0) throw new ArgumentException("neg"); }

    public static void Run(int x)
    {
        var p = Create(1);
        try { Work(x); }
        catch { Commit(p); throw; }
        Commit(p);
    }
}
