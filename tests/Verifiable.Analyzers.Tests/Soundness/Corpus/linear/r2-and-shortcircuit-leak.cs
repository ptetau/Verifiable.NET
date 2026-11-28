// Wrong: the commit only happens when ok is true, so the token leaks on the other path.
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

public static class AndLeak
{
    public static PendingCommit Create(int id) => new(id);

    [ConsumesObligation]
    public static bool TryCommit(PendingCommit c) { Ledger.Record(c.Id); return true; }

    public static bool Run(bool ok)
    {
        var p = Create(1);
        return ok && TryCommit(p);
    }
}
