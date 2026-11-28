// Wrong: `break` leaves the loop before the token made in this pass is committed.
// @expect: rejected
// @no-witness: a leaked token has no runtime effect
using System;
using System.Collections.Generic;
using Verifiable.Contracts;

namespace Corpus.Linear;

[MustConsume]
public readonly record struct PendingCommit(int Id);

public static class LoopBreakLeak
{
    public static PendingCommit Create(int id) => new(id);

    [ConsumesObligation]
    public static void Commit(PendingCommit c) { _ = c.Id; }

    public static void Run(int n)
    {
        for (var i = 0; i < n; i++)
        {
            var p = Create(i);
            if (i == 3)
            {
                break;
            }
            Commit(p);
        }
    }
}
