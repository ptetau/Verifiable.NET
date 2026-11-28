// Right: the token is committed exactly once, in the finally block, whether or not the work throws.
// @expect: proved
using System;
using System.Collections.Generic;
using Verifiable.Contracts;

namespace Corpus.Linear;

[MustConsume]
public readonly record struct PendingCommit(int Id);

public static class CommitInFinally
{
    public static PendingCommit Create(int id) => new(id);

    [ConsumesObligation]
    public static void Commit(PendingCommit c) { _ = c.Id; }

    public static int Run(int x)
    {
        var p = Create(x);
        try
        {
            x++;
        }
        finally
        {
            Commit(p);
        }
        return x;
    }
}
