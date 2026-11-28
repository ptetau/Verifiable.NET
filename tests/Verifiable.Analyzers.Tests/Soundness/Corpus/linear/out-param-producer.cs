// Right: Make hands its new token to the caller through an out parameter, and the caller commits it once.
// @expect: proved
// @known-bug: an out parameter starts as a live token, so assigning it is an overwrite leak and returning leaks it, and the caller's `out var p` is untracked (consumed before initialized)
using System;
using System.Collections.Generic;
using Verifiable.Contracts;

namespace Corpus.Linear;

[MustConsume]
public readonly record struct PendingCommit(int Id);

public static class OutProducer
{
    public static PendingCommit Create(int id) => new(id);

    [ConsumesObligation]
    public static void Commit(PendingCommit c) { _ = c.Id; }

    public static void Make(int id, out PendingCommit made)
    {
        made = Create(id);
    }

    public static void Run()
    {
        Make(1, out var p);
        Commit(p);
    }
}
