// Wrong: CommitAsync is started but its Task is dropped, so the commit may never finish (here it never does).
// @expect: rejected
// @no-witness: the dropped Task never completes, but nothing in the program observes that
using System;
using System.Collections.Generic;
using Verifiable.Contracts;

namespace Corpus.Linear;

[MustConsume]
public readonly record struct PendingCommit(int Id);

public static class AsyncNotAwaited
{
    public static PendingCommit Create(int id) => new(id);

    [ConsumesObligation]
    public static async System.Threading.Tasks.Task CommitAsync(PendingCommit c, System.Threading.Tasks.Task gate)
    {
        await gate;
        _ = c.Id;
    }

    public static void Run()
    {
        var p = Create(1);
        _ = CommitAsync(p, new System.Threading.Tasks.TaskCompletionSource().Task);
    }
}
