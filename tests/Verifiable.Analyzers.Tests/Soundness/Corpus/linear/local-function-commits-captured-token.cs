// Right: the local function commits the captured token, and it is called exactly once.
// @expect: proved
using System;
using System.Collections.Generic;
using Verifiable.Contracts;

namespace Corpus.Linear;

[MustConsume]
public readonly record struct PendingCommit(int Id);

public static class LocalFunctionCapture
{
    public static PendingCommit Create(int id) => new(id);

    [ConsumesObligation]
    public static void Commit(PendingCommit c) { _ = c.Id; }

    public static void Run()
    {
        var p = Create(1);
        Finish();

        void Finish() => Commit(p);
    }
}
