// Wrong: an iterator commits its token before and after a yield.
// @expect: rejected
// @witness: Corpus.Linear.Iter.Run()
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

public static class Iter
{
    public static PendingCommit Create(int id) => new(id);

    [ConsumesObligation]
    public static void Commit(PendingCommit c) => Ledger.Record(c.Id);

    public static IEnumerable<int> Gen()
    {
        var p = Create(1);
        Commit(p);
        yield return 1;
        Commit(p);
    }

    public static void Run() { foreach (var _ in Gen()) { } }
}
