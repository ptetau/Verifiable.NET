// Wrong: a new token is made only to read its Id, and is then dropped without being consumed.
// @expect: rejected
// @no-witness: a leaked token has no runtime effect
// @known-bug: a token produced inside a larger expression (`Create(1).Id`) is never tracked; only a whole-statement drop is reported
using System;
using System.Collections.Generic;
using Verifiable.Contracts;

namespace Corpus.Linear;

[MustConsume]
public readonly record struct PendingCommit(int Id);

public static class TempMemberRead
{
    public static PendingCommit Create(int id) => new(id);

    public static int Run()
    {
        var id = Create(1).Id;
        return id;
    }
}
