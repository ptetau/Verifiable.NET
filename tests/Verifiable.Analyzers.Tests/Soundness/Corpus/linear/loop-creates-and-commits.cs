// Right: each pass of the loop creates a fresh token and commits it before the next pass.
// @expect: proved
using System;
using System.Collections.Generic;
using Verifiable.Contracts;

namespace Corpus.Linear;

[MustConsume]
public readonly record struct PendingCommit(int Id);

public static class LoopCreateCommit
{
    public static PendingCommit Create(int id) => new(id);

    [ConsumesObligation]
    public static void Commit(PendingCommit c) { _ = c.Id; }

    public static void Run(int n)
    {
        for (var i = 0; i < n; i++)
        {
            var p = Create(i);
            if (i % 2 == 0)
            {
                Commit(p);
                continue;
            }
            Obligation.Discard(p, "odd");
        }
    }
}
