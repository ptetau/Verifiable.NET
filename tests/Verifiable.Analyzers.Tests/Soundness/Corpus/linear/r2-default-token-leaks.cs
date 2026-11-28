// Wrong: a default token is made and never consumed.
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

public static class DefTok
{
    public static void Run()
    {
        var p = default(PendingCommit);
        _ = p.Id;
    }
}
