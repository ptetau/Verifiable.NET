// Wrong: two catch clauses, the second one forgets to commit the token.
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

public static class SecondCatch
{
    public static PendingCommit Create(int id) => new(id);

    [ConsumesObligation]
    public static void Commit(PendingCommit c) => Ledger.Record(c.Id);

    public static void Work(int x) { if (x < 0) throw new ArgumentException("neg"); if (x == 0) throw new FormatException(); }

    public static void Run(int x)
    {
        var p = Create(1);
        try { Work(x); Commit(p); }
        catch (ArgumentException) { Commit(p); }
        catch (FormatException) { }
    }
}
