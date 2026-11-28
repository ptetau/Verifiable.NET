// Right: every arm of the switch, including default, consumes the token exactly once.
// @expect: proved
using System;
using System.Collections.Generic;
using Verifiable.Contracts;

namespace Corpus.Linear;

[MustConsume]
public readonly record struct PendingCommit(int Id);

public static class SwitchArms
{
    public static PendingCommit Create(int id) => new(id);

    [ConsumesObligation]
    public static void Commit(PendingCommit c) { _ = c.Id; }

    public static PendingCommit Run(int k)
    {
        var p = Create(k);
        switch (k)
        {
            case 0:
                Commit(p);
                break;
            case 1:
                Obligation.Discard(p, "one");
                break;
            case 2:
                return p;
            default:
                Commit(p);
                break;
        }
        return Create(k + 1);
    }
}
