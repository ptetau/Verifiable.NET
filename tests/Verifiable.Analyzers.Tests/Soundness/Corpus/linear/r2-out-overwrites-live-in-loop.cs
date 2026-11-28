// Wrong: an out argument overwrites a live token inside a loop, so the first token is never consumed.
// @expect: rejected
// @no-witness: a leaked token has no run-time symptom

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

public static class OutOverwrite
{
    public static PendingCommit Create(int id) => new(id);

    [ConsumesObligation]
    public static void Commit(PendingCommit c) => Ledger.Record(c.Id);

    public static void Make(int id, out PendingCommit made) { made = Create(id); }

    public static void Run(int n)
    {
        var p = Create(0);
        for (var i = 1; i <= n; i++)
        {
            Make(i, out p);
            Commit(p);
        }
    }
}
