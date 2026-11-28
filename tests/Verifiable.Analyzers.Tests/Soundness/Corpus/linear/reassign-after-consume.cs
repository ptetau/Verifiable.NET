// Right: the first token is committed before the variable is given a second token, which is then committed too.
// @expect: proved
// @known-bug: the statement `p = Create(2);` is reported as a dropped `<temporary>`: the assignment expression itself has the token type and is not marked consumed
using System;
using System.Collections.Generic;
using Verifiable.Contracts;

namespace Corpus.Linear;

[MustConsume]
public readonly record struct PendingCommit(int Id);

public static class Reassign
{
    public static PendingCommit Create(int id) => new(id);

    [ConsumesObligation]
    public static void Commit(PendingCommit c) { _ = c.Id; }

    public static void Run()
    {
        var p = Create(1);
        Commit(p);
        p = Create(2);
        Commit(p);
    }
}
