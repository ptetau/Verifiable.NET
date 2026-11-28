// Right: the token is deconstructed out of a tuple into `a`, and `a` is committed once.
// @expect: proved
using System;
using System.Collections.Generic;
using Verifiable.Contracts;

namespace Corpus.Linear;

[MustConsume]
public readonly record struct PendingCommit(int Id);

public static class TupleDeconstruct
{
    public static PendingCommit Create(int id) => new(id);

    [ConsumesObligation]
    public static void Commit(PendingCommit c) { _ = c.Id; }

    public static int Run()
    {
        var (a, b) = (Create(1), 1);
        Commit(a);
        return b;
    }
}
