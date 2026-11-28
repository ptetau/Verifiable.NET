// Right: every way out of the try (the early return and falling off the end) runs the finally block, which commits the token once.
// @expect: proved
using System;
using System.Collections.Generic;
using Verifiable.Contracts;

namespace Corpus.Linear;

[MustConsume]
public readonly record struct PendingCommit(int Id);

public static class ReturnThroughFinally
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
            x++;
        }
        finally
        {
            Commit(p);
        }
        return x;
    }
}
