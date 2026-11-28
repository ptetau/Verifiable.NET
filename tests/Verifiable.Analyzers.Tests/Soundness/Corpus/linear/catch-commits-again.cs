// Wrong: the token is committed before the try, and the catch block commits it a second time.
// @expect: rejected
// @witness: Corpus.Linear.CatchAgain.Run("not a number")
// @known-bug: catch regions are never reached by the worklist, so a second commit inside a catch block is not checked
using System;
using System.Collections.Generic;
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

public static class CatchAgain
{
    public static PendingCommit Create(int id) => new(id);

    [ConsumesObligation]
    public static void Commit(PendingCommit c) => Ledger.Record(c.Id);

    public static int Run(string text)
    {
        var p = Create(1);
        Commit(p);
        try
        {
            return int.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            Commit(p);
            return 0;
        }
    }
}
