// DateTime == compares ticks only; two records whose DateTimes share ticks (kinds differ) are equal, not different.
// @expect: rejected
// @witness: Corpus.Types.DtKind.Differ(new System.DateTime(5, System.DateTimeKind.Utc), new System.DateTime(5, System.DateTimeKind.Local))
using System;
using Verifiable.Contracts;

namespace Corpus.Types;

public sealed record Ev(DateTime At);

[Verifiable]
public static class DtKind
{
    public static bool Differ(DateTime a, DateTime b)
    {
        Spec.Requires(a.Ticks == b.Ticks, "Same ticks (kinds may differ).");
        Spec.Ensures<bool>(r => r == false, "Same ticks but any kinds, still equal.");
        return new Ev(a) == new Ev(b);
    }
}
