// Right: the token is IDisposable with a sink Dispose, so a using declaration consumes it once.
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

[MustConsume]
public readonly record struct Scoped(int Id) : IDisposable
{
    [ConsumesObligation]
    public void Dispose() => Ledger.Record(Id);
}

public static class UsingSink
{
    public static void Run()
    {
        using var s = new Scoped(1);
        _ = s.Id;
    }
}
