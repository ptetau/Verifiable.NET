// Wrong: a switch expression arm yields the existing token, which is then committed again.
// @expect: rejected
// @witness: Corpus.Linear.SwitchReuse.Run(0)
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

public static class SwitchReuse
{
    public static PendingCommit Create(int id) => new(id);

    [ConsumesObligation]
    public static void Commit(PendingCommit c) => Ledger.Record(c.Id);

    public static void Run(int n)
    {
        var p = Create(1);
        var q = n switch { 0 => p, _ => Create(2) };
        Commit(q);
        Commit(p);
    }
}
