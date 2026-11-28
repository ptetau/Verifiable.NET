// Right: only the token overload is a sink; the int overload is a plain call on a field of the token.
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

public static class Overloads
{
    public static PendingCommit Create(int id) => new(id);

    public static void Handle(int id) { _ = id; }

    [ConsumesObligation]
    public static void Handle(PendingCommit c) => Ledger.Record(c.Id);

    public static void Run()
    {
        var p = Create(1);
        Handle(p.Id);
        Handle(p);
    }
}
