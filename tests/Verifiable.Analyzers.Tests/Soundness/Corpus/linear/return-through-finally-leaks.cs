// Wrong: the early return runs a finally block that does not commit, so the token made before the try is never committed on that path.
// @expect: rejected
// @no-witness: a leaked token has no runtime effect
using System;
using System.Collections.Generic;
using Verifiable.Contracts;

namespace Corpus.Linear;

[MustConsume]
public readonly record struct PendingCommit(int Id);

public static class ReturnThroughFinallyLeak
{
    public static PendingCommit Create(int id) => new(id);

    [ConsumesObligation]
    public static void Commit(PendingCommit c) { _ = c.Id; }

    public static int Run(int x)
    {
        var p = Create(x);
        try
        {
            if (x > 10)
            {
                return 1;
            }
            Commit(p);
        }
        finally
        {
            x++;
        }
        return x;
    }
}
