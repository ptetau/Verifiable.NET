// Wrong: the ternary returns one of two live tokens, so the other leaks.
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

public static class TernRet
{
    public static PendingCommit Create(int id) => new(id);

    public static PendingCommit Pick(bool c)
    {
        var a = Create(1);
        var b = Create(2);
        return c ? a : b;
    }
}
