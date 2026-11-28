// Right: swapping two tokens with a tuple assignment keeps both live, and each is then committed once.
// @expect: proved
using System;
using System.Collections.Generic;
using Verifiable.Contracts;

namespace Corpus.Linear;

[MustConsume]
public readonly record struct PendingCommit(int Id);

public static class TupleSwap
{
    public static PendingCommit Create(int id) => new(id);

    [ConsumesObligation]
    public static void Commit(PendingCommit c) { _ = c.Id; }

    public static void Run()
    {
        var a = Create(1);
        var b = Create(2);
        (a, b) = (b, a);
        Commit(a);
        Commit(b);
    }
}
