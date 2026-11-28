// Wrong: the token is moved into a tuple, only the int half is used, and the token is never consumed.
// @expect: rejected
// @no-witness: a leaked token has no runtime effect
using System;
using System.Collections.Generic;
using Verifiable.Contracts;

namespace Corpus.Linear;

[MustConsume]
public readonly record struct PendingCommit(int Id);

public static class TupleHolds
{
    public static PendingCommit Create(int id) => new(id);

    public static int Run()
    {
        var pair = (Create(1), 1);
        return pair.Item2;
    }
}
