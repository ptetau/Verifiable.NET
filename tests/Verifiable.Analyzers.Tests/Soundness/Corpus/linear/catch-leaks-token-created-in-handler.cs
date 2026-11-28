// Wrong: the catch block makes a token and drops it without committing.
// @expect: rejected
// @no-witness: a leaked token has no runtime effect
using System;
using System.Collections.Generic;
using Verifiable.Contracts;

namespace Corpus.Linear;

[MustConsume]
public readonly record struct PendingCommit(int Id);

public static class CatchLeak
{
    public static PendingCommit Create(int id) => new(id);

    public static int Run(string text)
    {
        try
        {
            return int.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            var p = Create(1);
            return p.Id;
        }
    }
}
