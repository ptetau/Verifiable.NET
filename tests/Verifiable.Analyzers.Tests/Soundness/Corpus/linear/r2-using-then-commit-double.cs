// Wrong: a using block disposes (consumes) the token and then the body already committed it.
// @expect: rejected
// @witness: Corpus.Linear.UsingDouble.Run()
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

public static class UsingDouble
{
    [ConsumesObligation]
    public static void Done(Scoped s) => s.Dispose();

    public static void Run()
    {
        var s = new Scoped(1);
        using (s)
        {
            Done(s);
        }
    }
}
