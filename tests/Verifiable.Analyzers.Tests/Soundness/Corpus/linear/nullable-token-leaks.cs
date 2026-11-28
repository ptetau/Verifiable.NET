// Wrong: the token is stored in a `PendingCommit?` local and never consumed.
// @expect: rejected
// @no-witness: a leaked token has no runtime effect
// @known-bug: `PendingCommit?` is not a [MustConsume] type to IsMustConsumeType (Nullable<T> has no attribute), so a token stored in it is never tracked
using System;
using System.Collections.Generic;
using Verifiable.Contracts;

namespace Corpus.Linear;

[MustConsume]
public readonly record struct PendingCommit(int Id);

public static class NullableLeak
{
    public static PendingCommit Create(int id) => new(id);

    public static bool Run()
    {
        PendingCommit? held = Create(1);
        return held.HasValue;
    }
}
